namespace DbContextWithAudit.Tests.Model;

public sealed class ProductTag
{
    public int ProductId { get; set; }
    public int TagId { get; set; }
    public string Value { get; set; } = string.Empty;
}
