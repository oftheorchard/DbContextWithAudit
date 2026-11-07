using DbContextWithAudit.Tests.Model;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace DbContextWithAudit.Tests;

public sealed class AuditSaveChangesInterceptorExistingTransactionAsyncTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private AuditTestDbContext _context = null!;
    private IDbContextTransaction? _transaction;

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync();
        _context = CreateContext();
        await _context.Database.EnsureCreatedAsync();
        _transaction = await _context.Database.BeginTransactionAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_transaction != null)
        {
            try { await _transaction.RollbackAsync(); }
            catch (InvalidOperationException) { }
            await _transaction.DisposeAsync();
        }
        await _context.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Modified_entity_is_saved_to_audit_table_with_original_values()
    {
        var product = new Product { Name = "Original", Price = 10m };
        _context.Products.Add(product);
        await _context.SaveChangesAsync();

        product.Name = "Updated";
        product.Price = 20m;
        await _context.SaveChangesAsync();

        var audit = await _context.ProductLogs.SingleAsync();
        Assert.Equal(product.Id, audit.Id);
        Assert.Equal("Original", audit.Name);
        Assert.Equal(10m, audit.Price);
    }

    [Fact]
    public async Task Deleted_entity_is_saved_to_audit_table_with_original_values()
    {
        var product = new Product { Name = "To delete", Price = 15m };
        _context.Products.Add(product);
        await _context.SaveChangesAsync();

        _context.Products.Remove(product);
        await _context.SaveChangesAsync();

        var audit = await _context.ProductLogs.SingleAsync();
        Assert.Equal(product.Id, audit.Id);
        Assert.Equal("To delete", audit.Name);
        Assert.Equal(15m, audit.Price);
        Assert.Empty(await _context.Products.ToListAsync());
    }

    [Fact]
    public async Task Multiple_modified_entities_are_inserted_into_the_audit_table_in_batches()
    {
        var products = Enumerable.Range(1, 5)
            .Select(i => new Product { Name = $"Original {i}", Price = i })
            .ToList();

        _context.Products.AddRange(products);
        await _context.SaveChangesAsync();

        foreach (var product in products)
            product.Name += " updated";

        await _context.SaveChangesAsync();

        var audits = await _context.ProductLogs
            .OrderBy(x => x.Id)
            .ToListAsync();

        Assert.Equal(5, audits.Count);
        Assert.Equal(
            new[] { "Original 1", "Original 2", "Original 3", "Original 4", "Original 5" },
            audits.Select(x => x.Name).ToArray());
    }

    [Fact]
    public async Task Audit_insert_failure_does_not_rollback_a_caller_owned_transaction()
    {
        var product = new Product { Name = "Original", Price = 10m };
        _context.Products.Add(product);
        await _context.SaveChangesAsync();

        await _transaction!.CommitAsync();
        await _transaction.DisposeAsync();
        _transaction = await _context.Database.BeginTransactionAsync();

        await _context.Database.ExecuteSqlRawAsync("DROP TABLE [LOG_Products]");
        product.Name = "Updated";

        await Assert.ThrowsAsync<SqliteException>(() => _context.SaveChangesAsync());
        await _transaction.RollbackAsync();
        await _transaction.DisposeAsync();
        _transaction = null;

        await using var verificationContext = CreateContext();
        var persistedProduct = await verificationContext.Products.SingleAsync();
        Assert.Equal("Original", persistedProduct.Name);
    }

    [Fact]
    public async Task Complex_entity_changes_are_audited_for_parent_child_and_collection_rows()
    {
        var product = new Product
        {
            Name = "Original product",
            Price = 100m,
            Profile = new ProductProfile { Description = "Original description" },
            Tags =
            [
                new ProductTag { TagId = 1, Value = "Original first tag" },
                new ProductTag { TagId = 2, Value = "Deleted tag" }
            ]
        };

        _context.Products.Add(product);
        await _context.SaveChangesAsync();

        product.Name = "Updated product";
        product.Profile.Description = "Updated description";
        product.Tags[0].Value = "Updated first tag";
        product.Tags.RemoveAt(1);
        await _context.SaveChangesAsync();

        var productAudit = await _context.ProductLogs.SingleAsync();
        Assert.Equal("Original product", productAudit.Name);
        Assert.Equal(100m, productAudit.Price);

        var profileAudit = await _context.ProductProfileLogs.SingleAsync();
        Assert.Equal(product.Id, profileAudit.ProductId);
        Assert.Equal("Original description", profileAudit.Description);

        var tagAudits = await _context.ProductTagLogs
            .OrderBy(x => x.TagId)
            .ToListAsync();
        Assert.Equal(2, tagAudits.Count);
        Assert.Equal(1, tagAudits[0].TagId);
        Assert.Equal("Original first tag", tagAudits[0].Value);
        Assert.Equal(2, tagAudits[1].TagId);
        Assert.Equal("Deleted tag", tagAudits[1].Value);
    }

    private AuditTestDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AuditTestDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new AuditSaveChangesInterceptor(maxRowsPerBatch: 2))
            .Options;

        return new AuditTestDbContext(options);
    }
}
