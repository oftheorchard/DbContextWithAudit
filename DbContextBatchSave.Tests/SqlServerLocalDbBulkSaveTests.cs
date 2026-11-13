using System.Data.Common;
using DbContextWithAudit.Tests.Model;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DbContextBatchSave.Tests;

public sealed class SqlServerLocalDbBulkSaveTests : IAsyncLifetime
{
    private readonly string _connectionString;
    private SqlServerBulkTestDbContext _context = null!;

    public SqlServerLocalDbBulkSaveTests()
    {
        var connectionString = Environment.GetEnvironmentVariable("DBCONTEXTBATCHSAVE_SQLSERVER_CONNECTION")
            ?? "Server=(localdb)\\MSSQLLocalDB;Integrated Security=True;TrustServerCertificate=True";
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        builder["Initial Catalog"] = $"DbContextBatchSaveBehavior_{Guid.NewGuid():N}";
        _connectionString = builder.ConnectionString;
    }

    public async ValueTask InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<SqlServerBulkTestDbContext>()
            .UseSqlServer(_connectionString, sqlServer => sqlServer.CommandTimeout(0))
            .Options;
        _context = new SqlServerBulkTestDbContext(options);
        await _context.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _context.Database.EnsureDeletedAsync();
        await _context.DisposeAsync();
    }

    [Fact]
    public void SaveAddedChanges_inserts_owned_graph_and_leaves_existing_changes_pending()
    {
        var modifiedProduct = new Product { Name = "Before", Price = 1m };
        var deletedProduct = new Product { Name = "To delete", Price = 2m };
        _context.Products.AddRange(modifiedProduct, deletedProduct);
        _context.SaveChanges();
        modifiedProduct.Name = "After";
        _context.Products.Remove(deletedProduct);

        var addedProduct = new Product
        {
            Name = "Bulk product",
            Price = 3m,
            Profile = new ProductProfile { Description = "Bulk profile" },
            Tags =
            [
                new ProductTag { TagId = 1, Value = "First" },
                new ProductTag { TagId = 2, Value = "Second" }
            ]
        };
        _context.Products.Add(addedProduct);

        var affectedEntries = _context.SaveAddedChanges();

        Assert.True(affectedEntries >= 4);
        Assert.True(addedProduct.Id > 0);
        Assert.Equal(addedProduct.Id, addedProduct.Profile.ProductId);
        Assert.All(addedProduct.Tags, tag => Assert.Equal(addedProduct.Id, tag.ProductId));
        Assert.Equal(EntityState.Unchanged, _context.Entry(addedProduct).State);
        Assert.Equal(EntityState.Unchanged, _context.Entry(addedProduct.Profile).State);
        Assert.All(addedProduct.Tags, tag => Assert.Equal(EntityState.Unchanged, _context.Entry(tag).State));
        Assert.Equal(EntityState.Modified, _context.Entry(modifiedProduct).State);
        Assert.Equal(EntityState.Deleted, _context.Entry(deletedProduct).State);

        var savedProduct = _context.Products.AsNoTracking().Single(product => product.Id == addedProduct.Id);
        Assert.Equal("Bulk profile", savedProduct.Profile.Description);
        Assert.Equal(["First", "Second"], savedProduct.Tags.OrderBy(tag => tag.TagId).Select(tag => tag.Value));
        Assert.Equal("Before", _context.Products.AsNoTracking().Single(product => product.Id == modifiedProduct.Id).Name);
        Assert.True(_context.Products.AsNoTracking().Any(product => product.Id == deletedProduct.Id));
    }

    [Fact]
    public async Task SaveAddedChangesAsync_inserts_owned_graph_and_propagates_generated_key()
    {
        var product = new Product
        {
            Name = "Async bulk product",
            Price = 4m,
            Profile = new ProductProfile { Description = "Async bulk profile" },
            Tags = [new ProductTag { TagId = 1, Value = "Async tag" }]
        };
        _context.Products.Add(product);

        var affectedEntries = await _context.SaveAddedChangesAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(affectedEntries >= 3);
        Assert.True(product.Id > 0);
        Assert.Equal(product.Id, product.Profile.ProductId);
        Assert.Equal(product.Id, Assert.Single(product.Tags).ProductId);
        Assert.Equal(EntityState.Unchanged, _context.Entry(product).State);
        Assert.Equal(EntityState.Unchanged, _context.Entry(product.Profile).State);
        Assert.Equal(EntityState.Unchanged, _context.Entry(Assert.Single(product.Tags)).State);
    }

    private sealed class SqlServerBulkTestDbContext(DbContextOptions<SqlServerBulkTestDbContext> options) : DbContext(options)
    {
        public DbSet<Product> Products => Set<Product>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Product>().ToTable("Products");
            modelBuilder.Entity<Product>().OwnsOne(product => product.Profile, profile =>
            {
                profile.ToTable("ProductProfiles");
                profile.HasKey(value => value.ProductId);
            });
            modelBuilder.Entity<Product>().OwnsMany(product => product.Tags, tag =>
            {
                tag.ToTable("ProductTags");
                tag.WithOwner().HasForeignKey(value => value.ProductId);
                tag.HasKey(value => new { value.ProductId, value.TagId });
                tag.Property(value => value.TagId).ValueGeneratedNever();
            });
        }
    }
}
