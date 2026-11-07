using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
    private readonly ILogger<AuditSaveChangesInterceptor> _logger = logger ?? NullLogger<AuditSaveChangesInterceptor>.Instance;
    private readonly AuditDataPersister _auditDataPersister = new(maxRowsPerBatch, maxParametersPerCommand, logger);

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        => SavingChangesAsync(eventData, result).AsTask().GetAwaiter().GetResult();

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        var context = eventData.Context;
        if (context != null)
        {
            var entries = AuditDataPreparer.Prepare(context);
            if (entries.Count == 0)
                return base.SavingChangesAsync(eventData, result, cancellationToken);

            _pending[context] = entries;

            if (context.Database.CurrentTransaction == null && !_createdTransactions.ContainsKey(context))
            {
                var tx = context.Database.BeginTransactionAsync(cancellationToken).GetAwaiter().GetResult();
                _createdTransactions[context] = tx;
            }

            // persist within the transaction (existing or created)
            // return PersistPendingAndReturn(eventData, result, cancellationToken);
            PersistPendingAndReturn(eventData, result, cancellationToken)
                .ConfigureAwait(false).GetAwaiter().GetResult();
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    //private async ValueTask<InterceptionResult<int>> PersistPendingAndReturn(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken)
    private async Task<int> PersistPendingAndReturn(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken)
    //private async Task PersistPendingAndReturn(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken)
    {
        var context = eventData.Context!;
        DbTransaction? dbTx = null;
        if (_createdTransactions.TryGetValue(context, out var created))
            dbTx = created.GetDbTransaction();
        else if (context.Database.CurrentTransaction != null)
            dbTx = context.Database.CurrentTransaction.GetDbTransaction();

        _logger.LogDebug("Persisting pending audit entries for context {ContextType} in async saving flow.", context.GetType().Name);
        if (_pending.TryRemove(context, out var audits) && audits.Count > 0)
            await _auditDataPersister.PersistAsync(context, audits, dbTx).ConfigureAwait(false);

        return 1;
         //return await base.SavingChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
        => SavedChangesAsync(eventData, result).AsTask().GetAwaiter().GetResult();

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
        => SaveChangesFailedAsync(eventData).GetAwaiter().GetResult();

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

}
