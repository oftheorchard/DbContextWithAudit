namespace DbContextWithAudit.Tests.Model;

public sealed class ProductLog
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
}
