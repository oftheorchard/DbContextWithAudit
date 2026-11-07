namespace DbContextWithAudit.Tests.Model;

public sealed class ProductProfileLog
{
    public int ProductId { get; set; }
    public string Description { get; set; } = string.Empty;
}
