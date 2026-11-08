using Microsoft.Extensions.DependencyInjection;

namespace DbContextWithAuditOfCurrentData;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers AuditInterceptorOptions and an AuditSaveChangesInterceptor instance in DI.
    /// When registering DbContexts, retrieve the interceptor from DI and add it to DbContextOptions.
    /// </summary>
    public static IServiceCollection AddAuditInterceptor(this IServiceCollection services, Action<AuditInterceptorOptions>? configure = null)
    {
        var options = new AuditInterceptorOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.AddSingleton<AuditSaveChangesInterceptor>(sp =>
        {
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<AuditSaveChangesInterceptor>>();
            return new AuditSaveChangesInterceptor(options.MaxRowsPerBatch, options.MaxParametersPerCommand, logger);
        });

        return services;
    }
}