using StorageWatch.Config;
using StorageWatch.Config.Options;
using StorageWatch.Services;
using StorageWatch.Services.Alerting;
using StorageWatch.Services.Alerting.Plugins;
using StorageWatch.Services.CentralServer;
using StorageWatch.Services.Logging;
using StorageWatch.Services.Monitoring;
using StorageWatch.Services.AutoUpdate;
using StorageWatchAgent.Services.AutoUpdate;
using StorageWatch.Models;
using StorageWatch.Communication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.IO;
using System.Collections.Generic;

var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
var agentDirectory = Path.Combine(programData, "StorageWatch", "Agent");
Directory.CreateDirectory(agentDirectory);
var configPath = Path.Combine(agentDirectory, "AgentConfig.json");

if (!File.Exists(configPath))
{
    var defaultConfigPath = Path.Combine(AppContext.BaseDirectory, "Defaults", "AgentConfig.default.json");
    if (File.Exists(defaultConfigPath))
    {
        File.Copy(defaultConfigPath, configPath, overwrite: false);
        var logFilePath = LogDirectoryInitializer.GetLogFilePath("agent.log");
        var tempLogger = new RollingFileLogger(logFilePath);
        tempLogger.Log("[STARTUP] Default AgentConfig.json created at: " + configPath);
    }
}

// Configure and build the host using the generic host builder with Windows Service integration
var host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((context, config) =>
    {
        // appsettings.json is used only for host/logging concerns.
        config.Sources.Clear();
        config.SetBasePath(AppContext.BaseDirectory);
        config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
        config.AddJsonFile($"appsettings.{context.HostingEnvironment.EnvironmentName}.json", optional: true, reloadOnChange: true);
    })
    // UseWindowsService() enables this application to run as a Windows Service
    .UseWindowsService()
    .ConfigureServices((context, services) =>
    {
        // Load and validate authoritative runtime configuration from ProgramData
        var options = JsonConfigLoader.LoadAndValidate(configPath);

        var databasePath = Path.Combine(agentDirectory, "StorageWatch.db");
        options.Database.ConnectionString = $"Data Source={databasePath}";

        // Register options in the service container for dependency injection
        services.Configure<StorageWatchOptions>(cfg =>
        {
            cfg.Mode = options.Mode;
            cfg.General = options.General;
            cfg.Monitoring = options.Monitoring;
            cfg.Database = options.Database;
            cfg.Alerting = options.Alerting;
            cfg.Retention = options.Retention;
            cfg.AutoUpdate = options.AutoUpdate;
            cfg.SqlReporting = options.SqlReporting;
            cfg.CentralServer = options.CentralServer;
        });

        services.Configure<AutoUpdateOptions>(cfg =>
        {
            cfg.Enabled = options.AutoUpdate.Enabled;
            cfg.ManifestUrl = options.AutoUpdate.ManifestUrl;
            cfg.CheckIntervalMinutes = options.AutoUpdate.CheckIntervalMinutes;
        });

        services.Configure<CentralServerOptions>(cfg =>
        {
            cfg.ServerUrl = options.CentralServer.ServerUrl;
            cfg.CheckIntervalSeconds = options.CentralServer.CheckIntervalSeconds;
            cfg.ApiKey = options.CentralServer.ApiKey;
        });

        // Register option validators
        services.AddSingleton<IValidateOptions<StorageWatchOptions>, StorageWatchOptionsValidator>();
        services.AddSingleton<IValidateOptions<MonitoringOptions>, MonitoringOptionsValidator>();
        services.AddSingleton<IValidateOptions<SmtpOptions>, SmtpOptionsValidator>();
        services.AddSingleton<IValidateOptions<GroupMeOptions>, GroupMeOptionsValidator>();

        // Register the logger
        var logFilePath = LogDirectoryInitializer.GetLogFilePath("agent.log");
        services.AddSingleton(new RollingFileLogger(logFilePath));

        // ====================================================================
        // Plugin Architecture Registration
        // ====================================================================

        // Create and configure the plugin registry
        var registry = new AlertSenderPluginRegistry();
        registry.DiscoverPlugins(); // Discover all alert sender plugins in current assembly
        services.AddSingleton(registry);

        // Register individual alert sender plugins with their options
        services.AddTransient<SmtpAlertSender>(sp =>
            new SmtpAlertSender(
                options.Alerting.Smtp,
                sp.GetRequiredService<RollingFileLogger>()));

        services.AddTransient<GroupMeAlertSender>(sp =>
            new GroupMeAlertSender(
                options.Alerting.GroupMe,
                sp.GetRequiredService<RollingFileLogger>()));

        // Register plugins as IAlertSender for plugin manager resolution
        services.AddTransient<IAlertSender, SmtpAlertSender>(sp =>
            new SmtpAlertSender(
                options.Alerting.Smtp,
                sp.GetRequiredService<RollingFileLogger>()));

        services.AddTransient<IAlertSender, GroupMeAlertSender>(sp =>
            new GroupMeAlertSender(
                options.Alerting.GroupMe,
                sp.GetRequiredService<RollingFileLogger>()));

        // Register the plugin manager
        services.AddSingleton<AlertSenderPluginManager>();

        // ====================================================================
        // End Plugin Architecture Registration
        // ====================================================================

        // ====================================================================
        // IPC Communication Server Registration
        // ====================================================================

        // Register the IPC server as a hosted service for UI communication
        services.AddHostedService<ServiceCommunicationServer>();

        // ====================================================================
        // End IPC Communication Server Registration
        // ====================================================================

        services.AddSingleton<IDiskStatusProvider>(sp =>
            new DiskAlertMonitor(sp.GetRequiredService<IOptionsMonitor<StorageWatchOptions>>().CurrentValue));

        // Register CentralPublisher only when Mode == Agent
        if (options.Mode == StorageWatchMode.Agent)
        {
            services.AddHttpClient();
            services.AddHostedService<CentralPublisher>();
        }

        // Register the Worker as a hosted background service that will run continuously
        services.AddHostedService<Worker>();

        // ====================================================================
        // Auto-Update Services
        // ====================================================================

        services.AddHttpClient<IServiceUpdateChecker, ServiceUpdateChecker>();
        services.AddHttpClient<IServiceUpdateDownloader, ServiceUpdateDownloader>();
        services.AddHttpClient<IUnifiedUpdateChecker, UnifiedUpdateChecker>();
        services.AddSingleton<IUnifiedUpdateSnapshotStore, InMemoryUnifiedUpdateSnapshotStore>();
        services.AddSingleton<IUnifiedInstallCheckpointStore, UnifiedInstallCheckpointStore>();
        services.AddSingleton<IInstallPathResolver, InstallPathResolver>();
        services.AddSingleton<IUserSessionLauncher, UserSessionLauncher>();
        services.AddSingleton<IUnifiedInstallCheckpointValidator, UnifiedInstallCheckpointValidator>();
        services.AddSingleton<IUpdateRestartIntentProcessor, UpdateRestartIntentProcessor>();
        services.AddSingleton<IUnifiedInstallOrchestrator, UnifiedInstallOrchestrator>();
        services.AddHostedService<UnifiedInstallResumeService>();
        new RollingFileLogger(logFilePath).Log("[AUTOUPDATE] Registered UnifiedInstallResumeService as a hosted startup service after all required update-resume dependencies.");
        services.AddSingleton<IServiceRestartHandler, UpdaterServiceRestartHandler>();

        services.AddSingleton<IServiceUpdateInstaller, AgentUpdateHandoffInstaller>();
        services.AddSingleton<IAutoUpdateTimerFactory, AutoUpdateTimerFactory>();
        services.AddHttpClient<IPluginUpdateChecker, PluginUpdateChecker>();
        services.AddHttpClient<IPluginUpdateDownloader, PluginUpdateDownloader>();
        services.AddSingleton<IPluginUpdateInstaller, PluginUpdateInstaller>();
        services.AddHostedService<AutoUpdateWorker>();

        // ====================================================================
        // End Auto-Update Services
        // ====================================================================
    })
    .Build();

await host.RunAsync();

StorageWatchOptions CreateDefaultOptions()
{
    var defaultOptions = new StorageWatchOptions();
    defaultOptions.Alerting.EnableNotifications = false;
    defaultOptions.Mode = StorageWatchMode.Standalone;
    defaultOptions.AutoUpdate.Enabled = true;
    defaultOptions.AutoUpdate.CheckIntervalMinutes = 60;

    var systemDrive = Path.GetPathRoot(Environment.SystemDirectory);
    var driveLetter = string.IsNullOrWhiteSpace(systemDrive)
        ? "C:"
        : systemDrive.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    defaultOptions.Monitoring.Drives = new List<string> { driveLetter };
    return defaultOptions;
}