#if DEBUG
using Microsoft.EntityFrameworkCore;

namespace Silksong_Rando_Logic_Manager.Data;

public static class LogicDbContextFactoryRegistration
{
    public static IServiceCollection AddLogicDbContextFactory(
        this IServiceCollection services,
        string databasePath)
    {
        services.AddDbContextFactory<LogicDbContext>((provider, options) =>
            options.UseSqlite($"Data Source={databasePath}")
                .AddInterceptors(provider.GetRequiredService<Services.DebugEfContextDiagnosticInterceptor>()));
        var descriptor = services.Last(service => service.ServiceType == typeof(IDbContextFactory<LogicDbContext>));
        services.Remove(descriptor);
        services.Add(ServiceDescriptor.Describe(
            typeof(IDbContextFactory<LogicDbContext>),
            provider => new Services.DebugObservedLogicDbContextFactory(
                (IDbContextFactory<LogicDbContext>)ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!),
                provider.GetRequiredService<Services.DebugEfContextDiagnosticInterceptor>()),
            descriptor.Lifetime));
        return services;
    }
}
#endif
