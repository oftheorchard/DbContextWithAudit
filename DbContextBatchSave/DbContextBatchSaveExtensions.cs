using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace DbContextBatchSave;

public static class DbContextBatchSaveExtensions
{
    public static int SaveAddedChanges(this DbContext context, bool acceptAllChangesOnSuccess = true)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (SqlServerBulkSave.TrySave(context, acceptAllChangesOnSuccess, out var savedEntries))
            return savedEntries;

        context.ChangeTracker.DetectChanges();
        if (!context.ChangeTracker.Entries().Any(entry => entry.State == EntityState.Added))
            return 0;

        var pendingChanges = CapturePendingChanges(context);
        try
        {
            foreach (var pendingChange in pendingChanges)
                pendingChange.Entry.State = EntityState.Unchanged;

            return context.SaveChanges(acceptAllChangesOnSuccess);
        }
        finally
        {
            RestorePendingChanges(pendingChanges);
        }
    }

    public static async Task<int> SaveAddedChangesAsync(
        this DbContext context,
        bool acceptAllChangesOnSuccess = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.ChangeTracker.DetectChanges();
        if (!context.ChangeTracker.Entries().Any(entry => entry.State == EntityState.Added))
            return 0;

        var pendingChanges = CapturePendingChanges(context);
        try
        {
            foreach (var pendingChange in pendingChanges)
                pendingChange.Entry.State = EntityState.Unchanged;

            return await context.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }
        finally
        {
            RestorePendingChanges(pendingChanges);
        }
    }

    private static List<PendingEntityChange> CapturePendingChanges(DbContext context)
    {
        return context.ChangeTracker.Entries()
            .Where(entry => entry.State is EntityState.Modified or EntityState.Deleted)
            .Select(entry => new PendingEntityChange(
                entry,
                entry.State,
                entry.CurrentValues.Clone(),
                entry.OriginalValues.Clone(),
                entry.Properties
                    .Where(property => property.IsModified)
                    .Select(property => property.Metadata.Name)
                    .ToHashSet(StringComparer.Ordinal)))
            .ToList();
    }

    private static void RestorePendingChanges(IEnumerable<PendingEntityChange> pendingChanges)
    {
        foreach (var pendingChange in pendingChanges)
        {
            pendingChange.Entry.State = pendingChange.State;
            pendingChange.Entry.CurrentValues.SetValues(pendingChange.CurrentValues);
            pendingChange.Entry.OriginalValues.SetValues(pendingChange.OriginalValues);

            if (pendingChange.State != EntityState.Modified)
                continue;

            foreach (var property in pendingChange.Entry.Properties)
                property.IsModified = pendingChange.ModifiedProperties.Contains(property.Metadata.Name);
        }
    }

    private sealed record PendingEntityChange(
        EntityEntry Entry,
        EntityState State,
        PropertyValues CurrentValues,
        PropertyValues OriginalValues,
        HashSet<string> ModifiedProperties);
}
