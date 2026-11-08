using DbContextWithAudit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DbContextWithAudit.Tests;

internal sealed class AuditTestDbContextOptionBuilder(SqliteConnection connection)
{
    private int _maxRowsPerBatch = 500;

    public AuditTestDbContextOptionBuilder WithMaxRowsPerBatch(int maxRowsPerBatch)
    {
        _maxRowsPerBatch = maxRowsPerBatch;
        return this;
    }

    public AuditTestDbContext Build()
    {
        var options = new DbContextOptionsBuilder<AuditTestDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(new AuditSaveChangesInterceptor(_maxRowsPerBatch))
            .Options;

        return new AuditTestDbContext(options);
    }
}
