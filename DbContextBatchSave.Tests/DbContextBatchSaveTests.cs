using System.Diagnostics;
using DbContextBatchSave;
using DbContextWithAudit.Tests.Model;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DbContextBatchSave.Tests;

public sealed class DbContextBatchSaveTests : IAsyncLifetime
{
    //private const int PerformanceInsertCount = 1_000_000;
    private const int PerformanceInsertCount = 100_000;
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly ITestOutputHelper _output;
    private BatchSaveTestDbContext _context = null!;

    public DbContextBatchSaveTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync();
        _context = CreateContext();
        await _context.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public void SaveChanges_inserts_one_million_products_and_reports_elapsed_time()
    {
        _context.Products.AddRange(CreatePerformanceProducts());

        var stopwatch = Stopwatch.StartNew();
        var affectedEntries = _context.SaveChanges();
        stopwatch.Stop();

        _output.WriteLine($"EF Core SaveChanges: {stopwatch.Elapsed.TotalSeconds:F3} seconds for {PerformanceInsertCount:N0} products.");
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

        _output.WriteLine($"DbContextBatchSave SaveAddedChanges: {stopwatch.Elapsed.TotalSeconds:F3} seconds for {PerformanceInsertCount:N0} products.");
        Assert.True(affectedEntries >= PerformanceInsertCount);
        Assert.Equal(PerformanceInsertCount, _context.Products.AsNoTracking().Count());
    }

    [Fact]
    public void SaveAddedChanges_inserts_multiple_simple_entities()
    {
        var products = Enumerable.Range(1, 5)
            .Select(index => new Product
            {
                Name = $"Product {index}",
                Price = index
            })
            .ToList();
        _context.Products.AddRange(products);

        var affectedEntries = _context.SaveAddedChanges();

        Assert.True(affectedEntries >= products.Count);
        Assert.All(products, product => Assert.True(product.Id > 0));
        Assert.Equal(products.Count, _context.Products.Count());
    }

    [Fact]
    public async Task SaveAddedChangesAsync_inserts_owned_entity_graph_and_propagates_generated_key()
    {
        var product = new Product
        {
            Name = "Complex product",
            Price = 42m,
            Profile = new ProductProfile { Description = "Product profile" },
            Tags =
            [
                new ProductTag { TagId = 1, Value = "First" },
                new ProductTag { TagId = 2, Value = "Second" }
            ]
        };
        _context.Products.Add(product);

        var affectedEntries = await _context.SaveAddedChangesAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(affectedEntries >= 4);
        Assert.True(product.Id > 0);
        Assert.Equal(product.Id, product.Profile.ProductId);
        Assert.All(product.Tags, tag => Assert.Equal(product.Id, tag.ProductId));

        var savedProduct = await _context.Products
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Product profile", savedProduct.Profile.Description);
        Assert.Equal(["First", "Second"], savedProduct.Tags.OrderBy(tag => tag.TagId).Select(tag => tag.Value));
    }

    [Fact]
    public async Task SaveAddedChangesAsync_leaves_modified_entities_pending()
    {
        var existingProduct = new Product { Name = "Before", Price = 10m };
        _context.Products.Add(existingProduct);
        await _context.SaveChangesAsync(TestContext.Current.CancellationToken);

        existingProduct.Name = "After";
        var newProduct = new Product { Name = "New", Price = 20m };
        _context.Products.Add(newProduct);

        await _context.SaveAddedChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(EntityState.Modified, _context.Entry(existingProduct).State);
        Assert.True(_context.Entry(existingProduct).Property(product => product.Name).IsModified);
        Assert.Equal("Before", _context.Entry(existingProduct).Property(product => product.Name).OriginalValue);
        var savedExistingProduct = await _context.Products
            .AsNoTracking()
            .SingleAsync(product => product.Id == existingProduct.Id, TestContext.Current.CancellationToken);
        Assert.Equal("Before", savedExistingProduct.Name);
        Assert.True(newProduct.Id > 0);

        await _context.SaveChangesAsync(TestContext.Current.CancellationToken);
        savedExistingProduct = await _context.Products
            .AsNoTracking()
            .SingleAsync(product => product.Id == existingProduct.Id, TestContext.Current.CancellationToken);
        Assert.Equal("After", savedExistingProduct.Name);
    }

    [Fact]
    public async Task SaveAddedChangesAsync_leaves_deleted_entities_pending()
    {
        var existingProduct = new Product { Name = "To delete", Price = 10m };
        _context.Products.Add(existingProduct);
        await _context.SaveChangesAsync(TestContext.Current.CancellationToken);

        _context.Products.Remove(existingProduct);
        var newProduct = new Product { Name = "New", Price = 20m };
        _context.Products.Add(newProduct);

        await _context.SaveAddedChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(EntityState.Deleted, _context.Entry(existingProduct).State);
        Assert.True(await _context.Products.AsNoTracking()
            .AnyAsync(product => product.Id == existingProduct.Id, TestContext.Current.CancellationToken));
        Assert.True(newProduct.Id > 0);

        await _context.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.False(await _context.Products.AsNoTracking()
            .AnyAsync(product => product.Id == existingProduct.Id, TestContext.Current.CancellationToken));
    }

    private BatchSaveTestDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<BatchSaveTestDbContext>()
            .UseSqlite(_connection)
            .Options;

        return new BatchSaveTestDbContext(options);
    }

    private static List<Product> CreatePerformanceProducts()
    {
        return Enumerable.Range(0, PerformanceInsertCount)
            .Select(_ => new Product { Name = "Performance test product", Price = 1m })
            .ToList();
    }
}
