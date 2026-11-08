namespace DbContextWithAuditOfCurrentData.Tests.Model;

public sealed class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public ProductProfile Profile { get; set; } = new();
    public List<ProductTag> Tags { get; set; } = [];
}