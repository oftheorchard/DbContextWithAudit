using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DbContextWithAudit;

public static class ServiceCollectionDbContextExtensions
{
    /// <summary>
    /// Registers a DbContext and automatically wires the AuditSaveChangesInterceptor (resolved from DI) into the DbContext options.
    /// </summary>
    public static IServiceCollection AddDbContextWithAudit<TContext>(this IServiceCollection services,
        Action<DbContextOptionsBuilder>? optionsAction = null,
        ServiceLifetime contextLifetime = ServiceLifetime.Scoped,
        ServiceLifetime optionsLifetime = ServiceLifetime.Scoped)
        where TContext : DbContext
    {
        services.AddDbContext<TContext>((sp, options) =>
        {
            optionsAction?.Invoke(options);
            var interceptor = sp.GetService<AuditSaveChangesInterceptor>();
            if (interceptor != null)
                options.AddInterceptors(interceptor);
        }, contextLifetime, optionsLifetime);

        return services;
    }

    /// <summary>
    /// Registers a DbContext with an optionsAction that receives the IServiceProvider, and automatically
    /// wires the AuditSaveChangesInterceptor (resolved from DI) into the DbContext options.
    /// </summary>
    public static IServiceCollection AddDbContextWithAudit<TContext>(this IServiceCollection services,
        Action<IServiceProvider, DbContextOptionsBuilder> optionsAction,
        ServiceLifetime contextLifetime = ServiceLifetime.Scoped,
        ServiceLifetime optionsLifetime = ServiceLifetime.Scoped)
        where TContext : DbContext
    {
        services.AddDbContext<TContext>((sp, options) =>
        {
            optionsAction?.Invoke(sp, options);
            var interceptor = sp.GetService<AuditSaveChangesInterceptor>();
            if (interceptor != null)
                options.AddInterceptors(interceptor);
        }, contextLifetime, optionsLifetime);

        return services;
    }

    /// <summary>
    /// Registers a pooled DbContext and wires the AuditSaveChangesInterceptor (resolved from DI).
    /// </summary>
    public static IServiceCollection AddDbContextPoolWithAudit<TContext>(this IServiceCollection services,
        Action<DbContextOptionsBuilder>? optionsAction = null,
        int poolSize = 128)
        where TContext : DbContext
    {
        services.AddDbContextPool<TContext>((sp, options) =>
        {
            optionsAction?.Invoke(options);
            var interceptor = sp.GetService<AuditSaveChangesInterceptor>();
            if (interceptor != null)
                options.AddInterceptors(interceptor);
        }, poolSize);

        return services;
    }
}
