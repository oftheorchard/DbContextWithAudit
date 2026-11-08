using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbContextWithAudit;

/// <summary>
/// DbContext interceptor that saves previous versions of modified or deleted entities
/// into audit tables named with the prefix "LOG_" + original table name.
/// The interceptor works generically for all mapped entity types.
/// </summary>
public class AuditSaveChangesInterceptor(int maxRowsPerBatch = 500, int maxParametersPerCommand = -1, ILogger<AuditSaveChangesInterceptor>? logger = null) : SaveChangesInterceptor
{
    private readonly ILogger<AuditSaveChangesInterceptor> _logger = logger ?? NullLogger<AuditSaveChangesInterceptor>.Instance;
    private readonly AuditDataPersister _auditDataPersister = new(maxRowsPerBatch, maxParametersPerCommand, logger);
    private readonly AuditTransactionManager _auditTransactionManager = new(logger);

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
                var dbTransaction = await _auditTransactionManager
                    .EnsureTransactionAsync(context, cancellationToken);

                _logger.LogDebug("Persisting pending audit entries for context {ContextType} in async saving flow.", context.GetType().Name);
                await _auditDataPersister.PersistAsync(context, entries, dbTransaction);
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
            await _auditTransactionManager.CommitAsync(context, cancellationToken);
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
            await _auditTransactionManager.RollbackAsync(context, cancellationToken);
        }

        await base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

}
