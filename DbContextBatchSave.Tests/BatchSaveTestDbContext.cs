using DbContextWithAudit.Tests.Model;
using Microsoft.EntityFrameworkCore;

namespace DbContextBatchSave.Tests;

public sealed class BatchSaveTestDbContext(DbContextOptions<BatchSaveTestDbContext> options) : DbContext(options)
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
        });
    }
}
