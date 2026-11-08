using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;

namespace DbContextWithAuditOfCurrentData;

/// <summary>
/// DbContext interceptor that saves current values for added or modified entities and deleted values
/// into audit tables named with the prefix "LOG_" + original table name.
/// </summary>
public class AuditSaveChangesInterceptor(int maxRowsPerBatch = 500, int maxParametersPerCommand = -1, ILogger<AuditSaveChangesInterceptor>? logger = null) : SaveChangesInterceptor
{
    private readonly ILogger<AuditSaveChangesInterceptor> _logger = logger ?? NullLogger<AuditSaveChangesInterceptor>.Instance;
    private readonly AuditDataPersister _auditDataPersister = new(maxRowsPerBatch, maxParametersPerCommand, logger);
    private readonly AuditTransactionManager _auditTransactionManager = new(logger);
    private readonly ConcurrentDictionary<DbContext, List<PendingAuditEntry>> _pendingEntries = new();

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        => SavingChangesAsync(eventData, result).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        var context = eventData.Context;
        if (context != null)
        {
            var entries = AuditDataPreparer.Prepare(context);
            if (entries.Count > 0)
            {
                await _auditTransactionManager.EnsureTransactionAsync(context, cancellationToken);
                _pendingEntries[context] = entries;
            }
        }

        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
        => SavedChangesAsync(eventData, result).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        var context = eventData.Context;
        if (context != null)
        {
            try
            {
                if (_pendingEntries.TryRemove(context, out var pendingEntries))
                {
                    var entries = AuditDataPreparer.Materialize(pendingEntries);
                    var dbTransaction = context.Database.CurrentTransaction?.GetDbTransaction();
                    _logger.LogDebug("Persisting pending current-data audit entries for context {ContextType}.", context.GetType().Name);
                    await _auditDataPersister.PersistAsync(context, entries, dbTransaction);
                }

                await _auditTransactionManager.CommitAsync(context, cancellationToken);
            }
            catch
            {
                _pendingEntries.TryRemove(context, out _);
                await _auditTransactionManager.RollbackAsync(context, cancellationToken);
                throw;
            }
        }

        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
        => SaveChangesFailedAsync(eventData).GetAwaiter().GetResult();

    public override async Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        var context = eventData.Context;
        _logger.LogError(eventData.Exception, "SaveChanges failed for DbContext {ContextType} (async).", context?.GetType().Name);

        if (context != null)
        {
            _pendingEntries.TryRemove(context, out _);
            await _auditTransactionManager.RollbackAsync(context, cancellationToken);
        }

        await base.SaveChangesFailedAsync(eventData, cancellationToken);
    }
}