using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Data.Common;

namespace DbContextWithAudit;

/// <summary>
/// DbContext interceptor that saves previous versions of modified or deleted entities
/// into audit tables named with the prefix "LOG_" + original table name.
/// The interceptor works generically for all mapped entity types.
/// </summary>
public class AuditSaveChangesInterceptor(int maxRowsPerBatch = 500, int maxParametersPerCommand = -1, ILogger<AuditSaveChangesInterceptor>? logger = null) : SaveChangesInterceptor
{
    private readonly ConcurrentDictionary<DbContext, List<AuditEntry>> _pending = new();
    // Tracks transactions we created so we can commit/rollback them after SaveChanges completes
    private readonly ConcurrentDictionary<DbContext, IDbContextTransaction> _createdTransactions = new();
    // Maximum number of rows to include in a single batched INSERT statement
    private readonly int _maxRowsPerBatch = Math.Max(1, maxRowsPerBatch);
    // Maximum number of parameters allowed per command; if <= 0 the interceptor will auto-detect per-provider limits
    private readonly int _maxParametersPerCommand = maxParametersPerCommand < 64 ? -1 : maxParametersPerCommand;
    private readonly ILogger<AuditSaveChangesInterceptor> _logger = logger ?? NullLogger<AuditSaveChangesInterceptor>.Instance;
    private const int SqlServerParameterLimit = 2100;

    private sealed record AuditEntry(string AuditTableName, Dictionary<string, object?> ColumnValues);

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        => SavingChangesAsync(eventData, result).Result;

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

        _logger.LogDebug("Persisting pending audit entries for context {ContextType} in async saving flow.", context.GetType().Name);
        await PersistPendingAudits(context, dbTx).ConfigureAwait(false);
        return await base.SavingChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
        => SavedChangesAsync(eventData, result).Result;

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
                _logger.LogError(ex, "Error committing created transaction for DbContext {ContextType} (async).", context.GetType().Name);
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
        _logger.LogError(eventData.Exception, "SaveChanges failed for DbContext {ContextType}.", eventData.Context?.GetType().Name);

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
        _logger.LogError(eventData.Exception, "SaveChanges failed for DbContext {ContextType} (async).", eventData.Context?.GetType().Name);

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

    private static List<AuditEntry> CollectAuditEntries(DbContext context)
    {

        return [.. context.ChangeTracker.Entries()
            .Where(IsAuditableEntry)
            .Select(CreateAuditEntry)
            .OfType<AuditEntry>()];
    }

    private static bool IsAuditableEntry(EntityEntry entry) =>
        entry.State is EntityState.Modified or EntityState.Deleted;

    private static AuditEntry? CreateAuditEntry(EntityEntry entry)
    {
        var entityType = entry.Metadata;
        var tableName = GetTableName(entityType);
        return string.IsNullOrEmpty(tableName)
            ? null
            : new AuditEntry("LOG_" + tableName, GetOriginalColumnValues(entry, tableName));
    }

    private static Dictionary<string, object?> GetOriginalColumnValues(EntityEntry entry, string tableName)
    {
        var storeObject = StoreObjectIdentifier.Table(tableName, entry.Metadata.GetSchema());
        return entry.Properties
            .Where(property => property.Metadata.IsPrimaryKey() || !property.Metadata.IsShadowProperty())
            .ToDictionary(
                property => GetColumnName(property, storeObject),
                property => property.OriginalValue);
    }

    private static string GetColumnName(PropertyEntry property, StoreObjectIdentifier storeObject)
        => property.Metadata.GetColumnName(storeObject) ?? property.Metadata.Name;

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
            closeAfter = await OpenConnectionIfNeededAsync(connection);

            foreach (var group in audits.GroupBy(a => a.AuditTableName))
                await PersistAuditGroupAsync(context, connection, dbTransaction, group).ConfigureAwait(false);
        }
        finally
        {
            if (closeAfter)
                await connection.CloseAsync();
        }
    }

    private static async Task<bool> OpenConnectionIfNeededAsync(DbConnection connection)
    {
        if (connection.State == System.Data.ConnectionState.Open)
            return false;

        await connection.OpenAsync().ConfigureAwait(false);
        return true;
    }

    private async Task PersistAuditGroupAsync(
        DbContext context,
        DbConnection connection,
        DbTransaction? dbTransaction,
        IGrouping<string, AuditEntry> group)
    {
        var entries = group.ToList();
        var columnOrder = GetColumnOrder(entries);
        if (columnOrder.Count == 0)
        {
            _logger.LogDebug("No columns determined for audit table {AuditTable}, skipping.", group.Key);
            return;
        }

        var batchSize = GetBatchSize(context, columnOrder.Count);
        var sqlGenerationHelper = context.GetService<ISqlGenerationHelper>();
        _logger.LogDebug("EffectiveMaxParams={EffectiveMaxParams}, batchSize={BatchSize} for audit table {AuditTable}",
            GetEffectiveMaxParameters(context), batchSize, group.Key);

        for (var offset = 0; offset < entries.Count; offset += batchSize)
        {
            var batch = entries.Skip(offset).Take(batchSize).ToList();
            await ExecuteAuditBatchAsync(connection, dbTransaction, sqlGenerationHelper, group.Key, columnOrder, batch).ConfigureAwait(false);
        }
    }

    private static List<string> GetColumnOrder(IReadOnlyCollection<AuditEntry> entries)
    {
        var columns = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            columns.AddRange(from column in entry.ColumnValues.Keys
                             where seen.Add(column)
                             select column);
        }

        return columns;
    }

    private int GetBatchSize(DbContext context, int columnCount)
    {
        var maxRowsByParameters = Math.Max(1, GetEffectiveMaxParameters(context) / columnCount);
        return Math.Max(1, Math.Min(_maxRowsPerBatch, maxRowsByParameters));
    }

    private int GetEffectiveMaxParameters(DbContext context)
    {
        if (_maxParametersPerCommand > 0)
            return _maxParametersPerCommand;

        return context.Database.ProviderName?.Contains("SqlServer", StringComparison.OrdinalIgnoreCase) == true
            ? SqlServerParameterLimit
            : 2000;
    }

    private static async Task ExecuteAuditBatchAsync(
        DbConnection connection,
        DbTransaction? dbTransaction,
        ISqlGenerationHelper sqlGenerationHelper,
        string auditTableName,
        IReadOnlyList<string> columnOrder,
        IReadOnlyList<AuditEntry> batch)
    {
        using var command = connection.CreateCommand();
        command.Transaction = dbTransaction;

        var valueRows = new List<string>(batch.Count);
        for (var rowIndex = 0; rowIndex < batch.Count; rowIndex++)
            valueRows.Add(CreateParameterRow(command, batch[rowIndex], columnOrder, rowIndex));

        var tableIdentifier = sqlGenerationHelper.DelimitIdentifier(auditTableName);
        var columns = string.Join(", ", columnOrder.Select(sqlGenerationHelper.DelimitIdentifier));
        command.CommandText = $"INSERT INTO {tableIdentifier} ({columns}) VALUES {string.Join(", ", valueRows)};";
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static string CreateParameterRow(
        DbCommand command,
        AuditEntry entry,
        IReadOnlyList<string> columnOrder,
        int rowIndex)
    {
        var parameterNames = new List<string>(columnOrder.Count);
        for (var columnIndex = 0; columnIndex < columnOrder.Count; columnIndex++)
        {
            var parameterName = $"@p{rowIndex}_{columnIndex}";
            var parameter = command.CreateParameter();
            parameter.ParameterName = parameterName;
            parameter.Value = entry.ColumnValues.TryGetValue(columnOrder[columnIndex], out var value)
                ? value ?? DBNull.Value
                : DBNull.Value;
            command.Parameters.Add(parameter);
            parameterNames.Add(parameterName);
        }

        return $"({string.Join(", ", parameterNames)})";
    }
}
