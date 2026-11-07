using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace DbContextWithAudit;

internal sealed record AuditEntry(string AuditTableName, Dictionary<string, object?> ColumnValues);

internal sealed class AuditDataPreparer
{
    public List<AuditEntry> Prepare(DbContext context)
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

    private static string GetColumnName(PropertyEntry property, StoreObjectIdentifier storeObject) =>
        property.Metadata.GetColumnName(storeObject) ?? property.Metadata.Name;

    private static string? GetTableName(IEntityType entityType)
    {
        var tableName = entityType.GetTableName();
        if (!string.IsNullOrEmpty(tableName))
            return tableName;

        return entityType.ClrType.Name;
    }
}
