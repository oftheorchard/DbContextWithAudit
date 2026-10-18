using Microsoft.EntityFrameworkCore;

namespace DbContextWithAudit;

public static class DbContextOptionsBuilderExtensions
{
    /// <summary>
    /// Registers the AuditSaveChangesInterceptor to the DbContext options.
    /// Use when configuring the DbContext, for example in OnConfiguring or AddDbContext options.
    /// </summary>
    public static DbContextOptionsBuilder AddAuditInterceptor(this DbContextOptionsBuilder builder)
    {
        builder.AddInterceptors(new AuditSaveChangesInterceptor());
        return builder;
    }
}
