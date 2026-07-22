using LiveQs.Windows.Core;
using Microsoft.Extensions.DependencyInjection;

namespace LiveQs.Windows.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLiveQsInfrastructure(
        this IServiceCollection services,
        IAppPaths appPaths)
    {
        services.AddSingleton<IAppPaths>(appPaths);
        services.AddSingleton<IActivityRepository, SqliteActivityRepository>();
        services.AddSingleton<IForegroundSampler, ForegroundSampler>();
        services.AddSingleton<IStartupManager, StartupManager>();
        services.AddSingleton<ISyncStatusService, SyncStatusService>();
        services.AddSingleton<ISyncClient, CloudSyncClient>();
        services.AddHttpClient("cloud-sync", client => client.Timeout = TimeSpan.FromSeconds(15));
        services.AddHostedService<SamplingWorker>();
        services.AddHostedService<SyncWorker>();
        services.AddHostedService<MaintenanceWorker>();
        return services;
    }
}
