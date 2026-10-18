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
        return builder.AddAuditInterceptor(new AuditInterceptorOptions());
    }

    public static DbContextOptionsBuilder AddAuditInterceptor(this DbContextOptionsBuilder builder, int maxRowsPerBatch, int maxParametersPerCommand)
    {
        return builder.AddAuditInterceptor(new AuditInterceptorOptions { MaxRowsPerBatch = maxRowsPerBatch, MaxParametersPerCommand = maxParametersPerCommand });
    }

    public static DbContextOptionsBuilder AddAuditInterceptor(this DbContextOptionsBuilder builder, AuditInterceptorOptions options)
    {
        builder.AddInterceptors(new AuditSaveChangesInterceptor(options.MaxRowsPerBatch, options.MaxParametersPerCommand));
        return builder;
    }
}
