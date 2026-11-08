namespace DbContextWithAuditOfCurrentData.Tests.Model;

public sealed class ProductTagLog
{
    public int LogId { get; set; }
    public int ProductId { get; set; }
    public int TagId { get; set; }
    public string Value { get; set; } = string.Empty;
}