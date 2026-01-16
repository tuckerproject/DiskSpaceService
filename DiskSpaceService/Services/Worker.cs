using DiskSpaceService.Config;
using DiskSpaceService.Data;
using DiskSpaceService.Services.Alerting;
using DiskSpaceService.Services.Logging;
using DiskSpaceService.Services.Monitoring;
using DiskSpaceService.Services.Scheduling;
using Microsoft.Extensions.Hosting;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DiskSpaceService.Services
{
    public class Worker : BackgroundService
    {
        private readonly DiskSpaceConfig _config;
        private readonly RollingFileLogger _logger;
        private readonly SqlReporterScheduler _sqlScheduler;
        private readonly NotificationLoop _notificationLoop;

        public Worker()
        {
            string logDir = @"C:\ProgramData\DiskSpaceService\Logs";
            Directory.CreateDirectory(logDir);

            _logger = new RollingFileLogger(Path.Combine(logDir, "service.log"));

            var baseDir = AppContext.BaseDirectory;
            var configPath = Path.Combine(baseDir, "DiskSpaceConfig.xml");
            _config = ConfigLoader.Load(configPath);

            if (_config.EnableStartupLogging)
                _logger.Log("[STARTUP] Config loaded");

            var monitor = new DiskAlertMonitor(_config);
            var senders = AlertSenderFactory.BuildSenders(_config, _logger);
            var sqlReporter = new SqlReporter(_config, _logger);

            _sqlScheduler = new SqlReporterScheduler(_config, sqlReporter, _logger);
            _notificationLoop = new NotificationLoop(_config, senders, monitor, _logger);

            if (_config.EnableStartupLogging)
                _logger.Log("[STARTUP] Components initialized");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.Log("[WORKER] ExecuteAsync started");

            _ = _notificationLoop.RunAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                await _sqlScheduler.CheckAndRunAsync(DateTime.Now);
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }

            _logger.Log("[WORKER] ExecuteAsync exiting");
        }
    }
}