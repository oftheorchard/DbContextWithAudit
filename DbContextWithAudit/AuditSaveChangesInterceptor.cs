using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;

namespace DbContextWithAudit;

/// <summary>
/// DbContext interceptor that saves previous versions of modified or deleted entities
/// into audit tables named with the prefix "LOG_" + original table name.
/// The interceptor works generically for all mapped entity types.
/// </summary>
public class AuditSaveChangesInterceptor : SaveChangesInterceptor
{
    private readonly ConcurrentDictionary<DbContext, List<AuditEntry>> _pending = new();

    private record AuditEntry(string AuditTableName, Dictionary<string, object?> ColumnValues);

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        var context = eventData.Context;
        if (context == null)
            return base.SavingChanges(eventData, result);

        var entries = CollectAuditEntries(context);
        if (entries.Count > 0)
            _pending[context] = entries;

        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        var context = eventData.Context;
        if (context != null)
        {
            var entries = CollectAuditEntries(context);
            if (entries.Count > 0)
                _pending[context] = entries;
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        var context = eventData.Context;
        if (context != null)
            PersistPendingAudits(context).GetAwaiter().GetResult();

        return base.SavedChanges(eventData, result);
    }

    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        var context = eventData.Context;
        if (context != null)
            await PersistPendingAudits(context).ConfigureAwait(false);

        return await base.SavedChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        // cleanup pending on failure
        if (eventData.Context != null)
            _pending.TryRemove(eventData.Context, out _);

        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context != null)
            _pending.TryRemove(eventData.Context, out _);

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

    private async Task PersistPendingAudits(DbContext context)
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

            foreach (var audit in audits)
            {
                try
                {
                    using var cmd = connection.CreateCommand();
                    // Build insert SQL
                    var cols = string.Join(", ", audit.ColumnValues.Keys.Select(c => "[" + c + "]"));
                    var paramNames = audit.ColumnValues.Keys.Select((c, i) => "@p" + i).ToArray();
                    var values = string.Join(", ", paramNames);
                    cmd.CommandText = $"INSERT INTO {audit.AuditTableName} ({cols}) VALUES ({values});";

                    int p = 0;
                    foreach (var val in audit.ColumnValues.Values)
                    {
                        var param = cmd.CreateParameter();
                        param.ParameterName = "@p" + p++;
                        param.Value = val ?? DBNull.Value;
                        cmd.Parameters.Add(param);
                    }

                    await cmd.ExecuteNonQueryAsync();
                }
                catch (Exception)
                {
                    // swallow exceptions for individual audit inserts to avoid breaking user changes
                    // in production you may want to log these
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
