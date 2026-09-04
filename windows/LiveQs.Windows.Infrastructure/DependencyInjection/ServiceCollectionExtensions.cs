using LiveQs.Windows.Core.Abstractions;
using LiveQs.Windows.Infrastructure.Classification;
using LiveQs.Windows.Infrastructure.Configuration;
using LiveQs.Windows.Infrastructure.Maintenance;
using LiveQs.Windows.Infrastructure.Persistence.Sqlite;
using LiveQs.Windows.Infrastructure.Sampling;
using LiveQs.Windows.Infrastructure.Startup;
using LiveQs.Windows.Infrastructure.Sync;
using LiveQs.Windows.Infrastructure.Update;
using Microsoft.Extensions.DependencyInjection;

namespace LiveQs.Windows.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLiveQsInfrastructure(
        this IServiceCollection services,
        IAppPaths appPaths)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IAppPaths>(appPaths);
        services.AddSingleton<SqliteActivityRepository>();
        services.AddSingleton<IDatabaseInitializer>(sp => sp.GetRequiredService<SqliteActivityRepository>());
        services.AddSingleton<IActivityWriter>(sp => sp.GetRequiredService<SqliteActivityRepository>());
        services.AddSingleton<IActivityQueryService>(sp => sp.GetRequiredService<SqliteActivityRepository>());
        services.AddSingleton<ISettingsStore>(sp => sp.GetRequiredService<SqliteActivityRepository>());
        services.AddSingleton<ISyncQueueStore>(sp => sp.GetRequiredService<SqliteActivityRepository>());
        services.AddSingleton<IClassificationRuleStore>(sp => sp.GetRequiredService<SqliteActivityRepository>());
        services.AddSingleton<IActivityMaintenance>(sp => sp.GetRequiredService<SqliteActivityRepository>());
        services.AddSingleton<IForegroundSampler, ForegroundSampler>();
        services.AddSingleton<IStartupManager, StartupManager>();
        services.AddSingleton<ISyncStatusService, SyncStatusService>();
        services.AddSingleton<ISyncClient, CloudSyncClient>();
        services.AddSingleton<IHeartbeatClient, HeartbeatClient>();
        services.AddSingleton<IDiagnosticsClient, DiagnosticsClient>();
        services.AddSingleton<IClassificationRuleSync, ClassificationRuleSync>();
        services.AddSingleton<IReclassificationClient, ReclassificationClient>();
        services.AddSingleton<IAppVersion, AppVersion>();
        services.AddSingleton<IUpdateStatusService, UpdateStatusService>();
        services.AddSingleton<IUpdateCheckClient, UpdateCheckClient>();
        services.AddSingleton<IUpdateStateStore, UpdateStateStore>();
        services.AddHttpClient("cloud-sync", client => client.Timeout = TimeSpan.FromSeconds(15));
        services.AddHttpClient("update-check", client => client.Timeout = TimeSpan.FromSeconds(100));
        // Update artifacts are large self-contained packages; the manifest
        // stays on the short-timeout client and the download gets its own.
        services.AddHttpClient("update-download", client => client.Timeout = TimeSpan.FromMinutes(10));
        services.AddHostedService<SamplingWorker>();
        services.AddHostedService<SyncWorker>();
        services.AddHostedService<ReclassificationWorker>();
        services.AddHostedService<HeartbeatWorker>();
        services.AddHostedService<UpdateCheckWorker>();
        services.AddHostedService<MaintenanceWorker>();
        return services;
    }
}
