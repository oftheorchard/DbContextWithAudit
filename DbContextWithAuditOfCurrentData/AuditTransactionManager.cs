using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Data.Common;

namespace DbContextWithAuditOfCurrentData;

internal sealed class AuditTransactionManager(ILogger? logger = null)
{
    private readonly ConcurrentDictionary<DbContext, IDbContextTransaction> _createdTransactions = new();
    private readonly ILogger _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public async Task<DbTransaction?> EnsureTransactionAsync(DbContext context, CancellationToken cancellationToken)
    {
        if (context.Database.CurrentTransaction is { } currentTransaction)
            return currentTransaction.GetDbTransaction();

        if (!_createdTransactions.TryGetValue(context, out var transaction))
        {
            transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            _createdTransactions[context] = transaction;
        }

        return transaction.GetDbTransaction();
    }

    public Task CommitAsync(DbContext context, CancellationToken cancellationToken) =>
        CompleteTransactionAsync(context, commit: true, cancellationToken);

    public Task RollbackAsync(DbContext context, CancellationToken cancellationToken) =>
        CompleteTransactionAsync(context, commit: false, cancellationToken);

    private async Task CompleteTransactionAsync(
        DbContext context,
        bool commit,
        CancellationToken cancellationToken)
    {
        if (!_createdTransactions.TryRemove(context, out var transaction))
            return;

        try
        {
            if (commit)
            {
                await transaction.CommitAsync(cancellationToken);
                _logger.LogDebug("Committed created transaction for DbContext {ContextType}.", context.GetType().Name);
                return;
            }

            _logger.LogWarning("Rolling back created transaction for DbContext {ContextType} due to SaveChanges failure.", context.GetType().Name);
            await transaction.RollbackAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            if (commit)
                throw;

            _logger.LogError(ex, "Error rolling back created transaction for DbContext {ContextType}.", context.GetType().Name);
        }
        finally
        {
            transaction.Dispose();
        }
    }
}