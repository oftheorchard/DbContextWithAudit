using DbContextWithAuditOfCurrentData.Tests.Model;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using CurrentDataAuditInterceptor = DbContextWithAuditOfCurrentData.AuditSaveChangesInterceptor;

namespace DbContextWithAuditOfCurrentData.Tests;

public sealed class AuditCurrentDataInterceptorTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private CurrentDataTestDbContext _context = null!;

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
    public async Task Added_entity_is_saved_to_audit_table_with_current_values_and_generated_key()
    {
        var product = new Product { Name = "Inserted", Price = 12m };

        _context.Products.Add(product);
        await _context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var audit = await _context.ProductLogs.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(product.Id, audit.Id);
        Assert.Equal("Inserted", audit.Name);
        Assert.Equal(12m, audit.Price);
    }

    [Fact]
    public async Task Modified_entity_is_saved_to_audit_table_with_current_values()
    {
        var product = new Product { Name = "Original", Price = 10m };
        _context.Products.Add(product);
        await _context.SaveChangesAsync(TestContext.Current.CancellationToken);

        product.Name = "Updated";
        product.Price = 20m;
        await _context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var audits = await _context.ProductLogs
            .OrderBy(audit => audit.LogId)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, audits.Count);
        Assert.Equal(product.Id, audits[0].Id);
        Assert.Equal("Original", audits[0].Name);
        Assert.Equal(10m, audits[0].Price);
        Assert.Equal(product.Id, audits[1].Id);
        Assert.Equal("Updated", audits[1].Name);
        Assert.Equal(20m, audits[1].Price);
    }

    [Fact]
    public async Task Deleted_entity_is_saved_to_audit_table_with_deleted_values()
    {
        var product = new Product { Name = "To delete", Price = 15m };
        _context.Products.Add(product);
        await _context.SaveChangesAsync(TestContext.Current.CancellationToken);

        _context.Products.Remove(product);
        await _context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var audits = await _context.ProductLogs
            .OrderBy(audit => audit.LogId)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, audits.Count);
        Assert.All(audits, audit =>
        {
            Assert.Equal(product.Id, audit.Id);
            Assert.Equal("To delete", audit.Name);
            Assert.Equal(15m, audit.Price);
        });
        Assert.Empty(await _context.Products.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Product_lifecycle_preserves_insert_update_child_collection_and_delete_snapshots()
    {
        var product = new Product
        {
            Name = "Original product",
            Price = 100m,
            Profile = new ProductProfile { Description = "Original profile" },
            Tags =
            [
                new ProductTag { TagId = 1, Value = "Original first tag" },
                new ProductTag { TagId = 2, Value = "Second tag" }
            ]
        };

        _context.Products.Add(product);
        await _context.SaveChangesAsync(TestContext.Current.CancellationToken);

        product.Name = "Updated product";
        product.Price = 120m;
        await _context.SaveChangesAsync(TestContext.Current.CancellationToken);

        product.Profile.Description = "Updated profile";
        await _context.SaveChangesAsync(TestContext.Current.CancellationToken);

        product.Tags[0].Value = "Updated first tag";
        await _context.SaveChangesAsync(TestContext.Current.CancellationToken);

        _context.Products.Remove(product);
        await _context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var productAudits = await _context.ProductLogs
            .OrderBy(audit => audit.LogId)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, productAudits.Count);
        Assert.Equal(product.Id, productAudits[0].Id);
        Assert.Equal("Original product", productAudits[0].Name);
        Assert.Equal("Updated product", productAudits[1].Name);
        Assert.Equal("Updated product", productAudits[2].Name);
        Assert.Equal([100m, 120m, 120m], productAudits.Select(audit => audit.Price));

        var profileAudits = await _context.ProductProfileLogs
            .OrderBy(audit => audit.LogId)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, profileAudits.Count);
        Assert.All(profileAudits, audit => Assert.Equal(product.Id, audit.ProductId));
        Assert.Equal(["Original profile", "Updated profile", "Updated profile"], profileAudits.Select(audit => audit.Description));

        var tagAudits = await _context.ProductTagLogs
            .OrderBy(audit => audit.LogId)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(5, tagAudits.Count);
        Assert.Equal(
            ["Original first tag", "Updated first tag", "Updated first tag"],
            tagAudits.Where(audit => audit.TagId == 1).Select(audit => audit.Value));
        Assert.Equal(
            ["Second tag", "Second tag"],
            tagAudits.Where(audit => audit.TagId == 2).Select(audit => audit.Value));
        Assert.All(tagAudits, audit => Assert.Equal(product.Id, audit.ProductId));
        Assert.Empty(await _context.Products.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Audit_insert_failure_rolls_back_the_main_change()
    {
        var product = new Product { Name = "Original", Price = 10m };
        _context.Products.Add(product);
        await _context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await _context.Database.ExecuteSqlRawAsync("DROP TABLE [LOG_Products]", TestContext.Current.CancellationToken);
        product.Name = "Updated";

        await Assert.ThrowsAsync<SqliteException>(() => _context.SaveChangesAsync(TestContext.Current.CancellationToken));

        await using var verificationContext = CreateContext();
        var persistedProduct = await verificationContext.Products.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Original", persistedProduct.Name);
    }

    private CurrentDataTestDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CurrentDataTestDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new CurrentDataAuditInterceptor())
            .Options;

        return new CurrentDataTestDbContext(options);
    }
}