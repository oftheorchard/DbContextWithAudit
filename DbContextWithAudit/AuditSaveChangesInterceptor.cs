using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore.Storage;

namespace DbContextWithAudit;

/// <summary>
/// DbContext interceptor that saves previous versions of modified or deleted entities
/// into audit tables named with the prefix "LOG_" + original table name.
/// The interceptor works generically for all mapped entity types.
/// </summary>
public class AuditSaveChangesInterceptor : SaveChangesInterceptor
{
    private readonly ConcurrentDictionary<DbContext, List<AuditEntry>> _pending = new();
    // Tracks transactions we created so we can commit/rollback them after SaveChanges completes
    private readonly ConcurrentDictionary<DbContext, IDbContextTransaction> _createdTransactions = new();
    // Maximum number of rows to include in a single batched INSERT statement
    private readonly int _maxRowsPerBatch;
    // Maximum number of parameters allowed per command; if <= 0 the interceptor will auto-detect per-provider limits
    private readonly int _maxParametersPerCommand;
    private readonly ILogger<AuditSaveChangesInterceptor> _logger;
    private const int SqlServerParameterLimit = 2100;

    private record AuditEntry(string AuditTableName, Dictionary<string, object?> ColumnValues);

    public AuditSaveChangesInterceptor(int maxRowsPerBatch = 500, int maxParametersPerCommand = -1, ILogger<AuditSaveChangesInterceptor>? logger = null)
    {
        _maxRowsPerBatch = Math.Max(1, maxRowsPerBatch);
        _maxParametersPerCommand = maxParametersPerCommand < 64 ? -1 : maxParametersPerCommand; // allow auto-detect when < 64
        _logger = logger ?? NullLogger<AuditSaveChangesInterceptor>.Instance;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        var context = eventData.Context;
        if (context == null)
            return base.SavingChanges(eventData, result);

        var entries = CollectAuditEntries(context);
        if (entries.Count == 0)
            return base.SavingChanges(eventData, result);

        _pending[context] = entries;
        // Ensure the audit inserts will be performed inside the same transaction as SaveChanges.
        // If there's no ambient transaction, create one now and remember to commit it later.
        if (context.Database.CurrentTransaction == null && !_createdTransactions.ContainsKey(context))
        {
            var tx = context.Database.BeginTransaction();
            _createdTransactions[context] = tx;
        }

        // Persist audit rows now so they are included in the transaction we just created (or existing one).
        if (_pending.ContainsKey(context))
        {
            var dbTx = _createdTransactions.TryGetValue(context, out var created) ? created.GetDbTransaction() : context.Database.CurrentTransaction?.GetDbTransaction();
            try
            {
                _logger.LogDebug("Persisting {Count} pending audit entries for context {ContextType} inside transaction.", _pending[context].Count, context.GetType().Name);
            }
            catch { }
            PersistPendingAudits(context, dbTx).GetAwaiter().GetResult();
        }

        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        var context = eventData.Context;
        if (context != null)
        {
            var entries = CollectAuditEntries(context);
            if (entries.Count == 0)
                return base.SavingChangesAsync(eventData, result, cancellationToken);

            _pending[context] = entries;
        }

        if (context != null)
        {
            if (context.Database.CurrentTransaction == null && !_createdTransactions.ContainsKey(context))
            {
                var tx = context.Database.BeginTransactionAsync(cancellationToken).GetAwaiter().GetResult();
                _createdTransactions[context] = tx;
            }

            if (_pending.ContainsKey(context))
            {
                // persist within the transaction (existing or created)
                return PersistPendingAndReturn(eventData, result, cancellationToken);
            }
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private async ValueTask<InterceptionResult<int>> PersistPendingAndReturn(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken)
    {
        var context = eventData.Context!;
        DbTransaction? dbTx = null;
        if (_createdTransactions.TryGetValue(context, out var created))
            dbTx = created.GetDbTransaction();
        else if (context.Database.CurrentTransaction != null)
            dbTx = context.Database.CurrentTransaction.GetDbTransaction();

        try
        {
            _logger.LogDebug("Persisting pending audit entries for context {ContextType} in async saving flow.", context.GetType().Name);
        }
        catch { }
        await PersistPendingAudits(context, dbTx).ConfigureAwait(false);
        return await base.SavingChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        var context = eventData.Context;
        // If we created a transaction earlier, commit and dispose it now.
        if (context != null && _createdTransactions.TryRemove(context, out var tx))
        {
            try
            {
                tx.Commit();
                _logger.LogDebug("Committed created transaction for DbContext {ContextType}.", context.GetType().Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to commit created transaction for DbContext {ContextType}.", context.GetType().Name);
                throw;
            }
            finally
            {
                tx.Dispose();
            }
        }

        // cleanup pending audits for this context
        if (context != null)
            _pending.TryRemove(context, out _);

        return base.SavedChanges(eventData, result);
    }

    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        var context = eventData.Context;
        if (context != null && _createdTransactions.TryRemove(context, out var tx))
        {
            try
            {
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogDebug("Committed created transaction for DbContext {ContextType} (async).", context.GetType().Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to commit created transaction for DbContext {ContextType} (async).", context.GetType().Name);
                throw;
            }
            finally
            {
                tx.Dispose();
            }
        }

        if (context != null)
            _pending.TryRemove(context, out _);

        return await base.SavedChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        // cleanup pending on failure
        if (eventData.Context != null)
        {
            _pending.TryRemove(eventData.Context, out _);
            if (_createdTransactions.TryRemove(eventData.Context, out var tx))
            {
                try
                {
                    _logger.LogWarning("Rolling back created transaction for DbContext {ContextType} due to SaveChanges failure.", eventData.Context.GetType().Name);
                    tx.Rollback();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error rolling back created transaction for DbContext {ContextType}.", eventData.Context.GetType().Name);
                }
                finally { tx.Dispose(); }
            }
        }

        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context != null)
        {
            _pending.TryRemove(eventData.Context, out _);
            if (_createdTransactions.TryRemove(eventData.Context, out var tx))
            {
                try
                {
                    _logger.LogWarning("Rolling back created transaction for DbContext {ContextType} due to SaveChanges failure (async).", eventData.Context.GetType().Name);
                    tx.Rollback();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error rolling back created transaction for DbContext {ContextType} (async).", eventData.Context.GetType().Name);
                }
                finally { tx.Dispose(); }
            }
        }

        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    private List<AuditEntry> CollectAuditEntries(DbContext context)
    {
        var list = new List<AuditEntry>();

        var entries = context.ChangeTracker.Entries()
            .Where(e => e.State == EntityState.Modified || e.State == EntityState.Deleted);

        var model = context.Model;

        foreach (var entry in entries)
        {
            // Get the relational table name for the entity type
            var entityType = entry.Metadata;
            var tableName = GetTableName(entityType);
            if (string.IsNullOrEmpty(tableName))
                continue;

            var auditTableName = "LOG_" + tableName;

            var columnValues = new Dictionary<string, object?>();

            foreach (var property in entry.Properties)
            {
                // capture original value (previous version)
                if (!property.Metadata.IsPrimaryKey() && property.Metadata.IsShadowProperty())
                    continue; // skip shadow properties

                var columnName = property.Metadata.GetColumnName(StoreObjectIdentifier.Table(entityType.GetTableName(), entityType.GetSchema()));
                if (string.IsNullOrEmpty(columnName))
                    columnName = property.Metadata.Name;

                object? value = null;
                if (entry.State == EntityState.Deleted || entry.State == EntityState.Modified)
                    value = property.OriginalValue;

                columnValues[columnName] = value;
            }

            list.Add(new AuditEntry(auditTableName, columnValues));
        }

        return list;
    }

    private static string? GetTableName(IEntityType entityType)
    {
        // Prefer the explicit table name from mappings
        var tableName = entityType.GetTableName();
        if (!string.IsNullOrEmpty(tableName))
            return tableName;

        // fallback to the entity type name
        return entityType.ClrType.Name;
    }

    private async Task PersistPendingAudits(DbContext context, DbTransaction? dbTransaction)
    {
        if (!_pending.TryRemove(context, out var audits) || audits == null || audits.Count == 0)
            return;

        var connection = context.Database.GetDbConnection();
        var closeAfter = false;
        try
        {
            if (connection.State != System.Data.ConnectionState.Open)
            {
                await connection.OpenAsync();
                closeAfter = true;
            }

            // Group audit entries by audit table name and execute batched inserts per table
            var groups = audits.GroupBy(a => a.AuditTableName);
            foreach (var group in groups)
            {
                try
                {
                    var entries = group.ToList();
                    // Determine ordered union of columns across all entries
                    var columnOrder = new List<string>();
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var e in entries)
                    {
                        foreach (var col in e.ColumnValues.Keys)
                        {
                            if (seen.Add(col))
                                columnOrder.Add(col);
                        }
                    }

                    if (columnOrder.Count == 0)
                    {
                        try { _logger.LogDebug("No columns determined for audit table {AuditTable}, skipping.", group.Key); } catch { }
                        continue;
                    }

                    // Determine maximum rows per batch considering provider parameter limits
                    var cols = string.Join(", ", columnOrder.Select(c => "[" + c + "]"));
                    int columnsCount = columnOrder.Count;
                    int effectiveMaxParams = _maxParametersPerCommand > 0
                        ? _maxParametersPerCommand
                        : (context.Database.ProviderName != null && context.Database.ProviderName.Contains("SqlServer", StringComparison.OrdinalIgnoreCase)
                            ? SqlServerParameterLimit
                            : 2000);

                    int maxRowsByParams = columnsCount > 0 ? Math.Max(1, effectiveMaxParams / columnsCount) : _maxRowsPerBatch;
                    int batchSize = Math.Max(1, Math.Min(_maxRowsPerBatch, maxRowsByParams));

                    _logger.LogDebug("EffectiveMaxParams={EffectiveMaxParams}, batchSize={BatchSize} for audit table {AuditTable}", effectiveMaxParams, batchSize, group.Key);

                    for (int offset = 0; offset < entries.Count; offset += batchSize)
                    {
                        var batch = entries.Skip(offset).Take(batchSize).ToList();
                        try
                        {
                            using var cmd = connection.CreateCommand();
                            if (dbTransaction != null)
                                cmd.Transaction = dbTransaction;

                            var valueRows = new List<string>();
                            int rowIndex = 0;
                            foreach (var entry in batch)
                            {
                                var paramNames = new List<string>();
                                for (int ci = 0; ci < columnOrder.Count; ci++)
                                {
                                    var paramName = "@p" + rowIndex + "_" + ci;
                                    paramNames.Add(paramName);

                                    var param = cmd.CreateParameter();
                                    param.ParameterName = paramName;
                                    if (entry.ColumnValues.TryGetValue(columnOrder[ci], out var val))
                                        param.Value = val ?? DBNull.Value;
                                    else
                                        param.Value = DBNull.Value;

                                    cmd.Parameters.Add(param);
                                }

                                valueRows.Add("(" + string.Join(", ", paramNames) + ")");
                                rowIndex++;
                            }

                            cmd.CommandText = $"INSERT INTO {group.Key} ({cols}) VALUES {string.Join(", ", valueRows)};";
                            await cmd.ExecuteNonQueryAsync();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to insert audit batch into {AuditTable}. BatchSize={BatchSize}. The transaction will be rolled back.", group.Key, batch.Count);
                            throw;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to process audit group {AuditTable}. The transaction will be rolled back.", group.Key);
                    throw;
                }
            }
        }
        finally
        {
            if (closeAfter)
                await connection.CloseAsync();
        }
    }
}
