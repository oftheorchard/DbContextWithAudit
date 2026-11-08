namespace DbContextWithAuditOfCurrentData.Tests.Model;

public sealed class ProductProfile
{
    public int ProductId { get; set; }
    public string Description { get; set; } = string.Empty;
}