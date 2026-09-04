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
        // LiveQs is a local-first LAN app: all traffic to the Owner-configured
        // server URL must bypass the system proxy. Day 0 incident (2026-09-04):
        // a local accelerator (Watt Toolkit, 127.0.0.1:26561) sat in the system
        // proxy, intercepted calls to http://localhost:8787, and answered with
        // its own 404 — sync, heartbeats, and diagnostics never reached the
        // server while sampling kept running.
        // The update channel below deliberately KEEPS the system proxy: fetching
        // the release manifest from GitHub is exactly the traffic such
        // accelerators exist to help with, so it must not bypass them. This
        // asymmetry is a product constraint, not an oversight.
        services.AddHttpClient("cloud-sync", client => client.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseProxy = false });
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
