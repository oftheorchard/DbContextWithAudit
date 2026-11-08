namespace DbContextWithAuditOfCurrentData.Tests.Model;

public sealed class ProductProfileLog
{
    public int LogId { get; set; }
    public int ProductId { get; set; }
    public string Description { get; set; } = string.Empty;
}