using System.Data.Common;
using System.Diagnostics;
using DbContextWithAudit.Tests.Model;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DbContextBatchSave.Tests;

public sealed class SqlServerLocalDbSavePerformanceTests : IAsyncLifetime
{
    //private const int PerformanceInsertCount = 1_000_000;
    private const int PerformanceInsertCount = 100_000;
    private readonly ITestOutputHelper _output;
    private readonly string _connectionString;
    private BatchSaveTestDbContext _context = null!;

    public SqlServerLocalDbSavePerformanceTests(ITestOutputHelper output)
    {
        _output = output;
        var connectionString = Environment.GetEnvironmentVariable("DBCONTEXTBATCHSAVE_SQLSERVER_CONNECTION")
            ?? "Server=(localdb)\\MSSQLLocalDB;Integrated Security=True;TrustServerCertificate=True";
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        builder["Initial Catalog"] = $"DbContextBatchSavePerf_{Guid.NewGuid():N}";
        _connectionString = builder.ConnectionString;
    }

    public async ValueTask InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<BatchSaveTestDbContext>()
            .UseSqlServer(_connectionString, sqlServer => sqlServer.CommandTimeout(0))
            .Options;
        _context = new BatchSaveTestDbContext(options);
        await _context.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _context.Database.EnsureDeletedAsync();
        await _context.DisposeAsync();
    }

    [Fact]
    public void SaveChanges_inserts_one_million_products_and_reports_elapsed_time()
    {
        _context.Products.AddRange(CreatePerformanceProducts());

        var stopwatch = Stopwatch.StartNew();
        var affectedEntries = _context.SaveChanges();
        stopwatch.Stop();

        _output.WriteLine($"SQL Server EF Core SaveChanges: {stopwatch.Elapsed.TotalSeconds:F3} seconds for {PerformanceInsertCount:N0} products.");
        Assert.True(affectedEntries >= PerformanceInsertCount);
        Assert.Equal(PerformanceInsertCount, _context.Products.AsNoTracking().Count());
    }

    [Fact]
    public void SaveAddedChanges_inserts_one_million_products_and_reports_elapsed_time()
    {
        _context.Products.AddRange(CreatePerformanceProducts());

        var stopwatch = Stopwatch.StartNew();
        var affectedEntries = _context.SaveAddedChanges();
        stopwatch.Stop();

        _output.WriteLine($"SQL Server DbContextBatchSave SaveAddedChanges: {stopwatch.Elapsed.TotalSeconds:F3} seconds for {PerformanceInsertCount:N0} products.");
        Assert.True(affectedEntries >= PerformanceInsertCount);
        Assert.Equal(PerformanceInsertCount, _context.Products.AsNoTracking().Count());
    }

    private static List<Product> CreatePerformanceProducts()
    {
        return Enumerable.Range(0, PerformanceInsertCount)
            .Select(_ => new Product { Name = "Performance test product", Price = 1m })
            .ToList();
    }
}
