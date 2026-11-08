namespace DbContextWithAuditOfCurrentData.Tests.Model;

public sealed class ProductLog
{
    public int LogId { get; set; }
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
}