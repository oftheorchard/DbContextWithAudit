using DbContextWithAudit.Tests.Model;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace DbContextWithAudit.Tests;

public sealed class AuditSaveChangesInterceptorExistingTransactionSyncTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AuditTestDbContext _context;
    private IDbContextTransaction? _transaction;

    public AuditSaveChangesInterceptorExistingTransactionSyncTests()
    {
        _connection.Open();
        _context = CreateContext();
        _context.Database.EnsureCreated();
        _transaction = _context.Database.BeginTransaction();
    }

    public void Dispose()
    {
        if (_transaction != null)
        {
            try { _transaction.Rollback(); }
            catch (InvalidOperationException) { }
            _transaction.Dispose();
        }
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public void Modified_entity_is_saved_to_audit_table_with_original_values()
    {
        var product = new Product { Name = "Original", Price = 10m };
        _context.Products.Add(product);
        _context.SaveChanges();

        product.Name = "Updated";
        product.Price = 20m;
        _context.SaveChanges();

        var audit = _context.ProductLogs.Single();
        Assert.Equal(product.Id, audit.Id);
        Assert.Equal("Original", audit.Name);
        Assert.Equal(10m, audit.Price);
    }

    [Fact]
    public void Deleted_entity_is_saved_to_audit_table_with_original_values()
    {
        var product = new Product { Name = "To delete", Price = 15m };
        _context.Products.Add(product);
        _context.SaveChanges();

        _context.Products.Remove(product);
        _context.SaveChanges();

        var audit = _context.ProductLogs.Single();
        Assert.Equal(product.Id, audit.Id);
        Assert.Equal("To delete", audit.Name);
        Assert.Equal(15m, audit.Price);
        Assert.Empty(_context.Products.ToList());
    }

    [Fact]
    public void Multiple_modified_entities_are_inserted_into_the_audit_table_in_batches()
    {
        var products = Enumerable.Range(1, 5)
            .Select(i => new Product { Name = $"Original {i}", Price = i })
            .ToList();

        _context.Products.AddRange(products);
        _context.SaveChanges();

        foreach (var product in products)
            product.Name += " updated";

        _context.SaveChanges();

        var audits = _context.ProductLogs
            .OrderBy(x => x.Id)
            .ToList();

        Assert.Equal(5, audits.Count);
        Assert.Equal(
            [ "Original 1", "Original 2", "Original 3", "Original 4", "Original 5" ],
            [.. audits.Select(x => x.Name)]);
    }

    [Fact]
    public void Audit_insert_failure_does_not_rollback_a_caller_owned_transaction()
    {
        var product = new Product { Name = "Original", Price = 10m };
        _context.Products.Add(product);
        _context.SaveChanges();

        _transaction!.Commit();
        _transaction.Dispose();
        _transaction = _context.Database.BeginTransaction();

        _context.Database.ExecuteSqlRaw("DROP TABLE [LOG_Products]");
        product.Name = "Updated";

        Assert.Throws<SqliteException>(() => _context.SaveChanges());
        _transaction.Rollback();
        _transaction.Dispose();
        _transaction = null;

        using var verificationContext = CreateContext();
        var persistedProduct = verificationContext.Products.Single();
        Assert.Equal("Original", persistedProduct.Name);
    }

    [Fact]
    public void Complex_entity_changes_are_audited_for_parent_child_and_collection_rows()
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
        _context.SaveChanges();

        product.Name = "Updated product";
        product.Profile.Description = "Updated description";
        product.Tags[0].Value = "Updated first tag";
        product.Tags.RemoveAt(1);
        _context.SaveChanges();

        var productAudit = _context.ProductLogs.Single();
        Assert.Equal("Original product", productAudit.Name);
        Assert.Equal(100m, productAudit.Price);

        var profileAudit = _context.ProductProfileLogs.Single();
        Assert.Equal(product.Id, profileAudit.ProductId);
        Assert.Equal("Original description", profileAudit.Description);

        var tagAudits = _context.ProductTagLogs.OrderBy(x => x.TagId).ToList();
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
