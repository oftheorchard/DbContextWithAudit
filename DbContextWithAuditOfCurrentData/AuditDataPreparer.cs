using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace DbContextWithAuditOfCurrentData;

internal sealed record AuditEntry(string AuditTableName, Dictionary<string, object?> ColumnValues);

internal sealed record PendingAuditEntry(
    string AuditTableName,
    EntityEntry Entry,
    EntityState State,
    Dictionary<string, object?>? DeletedColumnValues);

internal static class AuditDataPreparer
{
    public static List<PendingAuditEntry> Prepare(DbContext context) =>
        [.. context.ChangeTracker.Entries()
            .Where(IsAuditableEntry)
            .Select(CreatePendingAuditEntry)
            .OfType<PendingAuditEntry>()];

    public static List<AuditEntry> Materialize(IReadOnlyCollection<PendingAuditEntry> pendingEntries) =>
        [.. pendingEntries.Select(MaterializeEntry)];

    private static bool IsAuditableEntry(EntityEntry entry) =>
        entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted;

    private static PendingAuditEntry? CreatePendingAuditEntry(EntityEntry entry)
    {
        var tableName = GetTableName(entry.Metadata);
        if (string.IsNullOrEmpty(tableName))
            return null;

        var deletedColumnValues = entry.State == EntityState.Deleted
            ? GetColumnValues(entry, tableName, useOriginalValues: true)
            : null;
        return new PendingAuditEntry("LOG_" + tableName, entry, entry.State, deletedColumnValues);
    }

    private static AuditEntry MaterializeEntry(PendingAuditEntry pendingEntry)
    {
        var columnValues = pendingEntry.State == EntityState.Deleted
            ? pendingEntry.DeletedColumnValues!
            : GetColumnValues(pendingEntry.Entry, GetTableName(pendingEntry.Entry.Metadata)!, useOriginalValues: false);

        return new AuditEntry(pendingEntry.AuditTableName, columnValues);
    }

    private static Dictionary<string, object?> GetColumnValues(EntityEntry entry, string tableName, bool useOriginalValues)
    {
        var storeObject = StoreObjectIdentifier.Table(tableName, entry.Metadata.GetSchema());
        return entry.Properties
            .Where(property => property.Metadata.IsPrimaryKey() || !property.Metadata.IsShadowProperty())
            .ToDictionary(
                property => GetColumnName(property, storeObject),
                property => useOriginalValues ? property.OriginalValue : property.CurrentValue);
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