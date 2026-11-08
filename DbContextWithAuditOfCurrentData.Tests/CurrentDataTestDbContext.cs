using DbContextWithAuditOfCurrentData.Tests.Model;
using Microsoft.EntityFrameworkCore;

namespace DbContextWithAuditOfCurrentData.Tests;

public sealed class CurrentDataTestDbContext(DbContextOptions<CurrentDataTestDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductLog> ProductLogs => Set<ProductLog>();
    public DbSet<ProductProfileLog> ProductProfileLogs => Set<ProductProfileLog>();
    public DbSet<ProductTagLog> ProductTagLogs => Set<ProductTagLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>().ToTable("Products");
        modelBuilder.Entity<ProductLog>(log =>
        {
            log.ToTable("LOG_Products");
            log.HasKey(value => value.LogId);
        });

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

        modelBuilder.Entity<ProductProfileLog>(log =>
        {
            log.ToTable("LOG_ProductProfiles");
            log.HasKey(value => value.LogId);
        });

        modelBuilder.Entity<ProductTagLog>(log =>
        {
            log.ToTable("LOG_ProductTags");
            log.HasKey(value => value.LogId);
        });
    }
}