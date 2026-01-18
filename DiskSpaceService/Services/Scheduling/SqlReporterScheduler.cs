using DiskSpaceService.Config;
using DiskSpaceService.Services.Logging;
using System;
using System.IO;
using System.Threading.Tasks;

namespace DiskSpaceService.Services.Scheduling
{
    public class SqlReporterScheduler
    {
        private readonly DiskSpaceConfig _config;
        private readonly SqlReporter _reporter;
        private readonly RollingFileLogger _logger;

        private readonly string _lastRunPath =
            Path.Combine(AppContext.BaseDirectory, "last_sql_run.txt");

        public SqlReporterScheduler(
            DiskSpaceConfig config,
            SqlReporter reporter,
            RollingFileLogger logger)
        {
            _config = config;
            _reporter = reporter;
            _logger = logger;
        }

        public async Task CheckAndRunAsync(DateTime now)
        {
            if (!_config.EnableSqlReporting)
                return;

            DateTime scheduled = now.Date + _config.CollectionTime;
            DateTime? lastRun = LoadLastRun();

            bool shouldRun = false;

            if (_config.RunOnlyOncePerDay)
            {
                if (lastRun == null || lastRun.Value.Date < now.Date)
                {
                    if (now >= scheduled)
                        shouldRun = true;
                }
                else if (_config.RunMissedCollection && now > scheduled && lastRun.Value < scheduled)
                {
                    shouldRun = true;
                }
            }
            else
            {
                if (now >= scheduled)
                    shouldRun = true;
            }

            if (shouldRun)
            {
                _logger.Log("[SCHEDULER] Run: scheduled or missed run");
                await _reporter.WriteDailyReportAsync();
                SaveLastRun(now);
            }
        }

        private DateTime? LoadLastRun()
        {
            try
            {
                if (!File.Exists(_lastRunPath))
                    return null;

                string text = File.ReadAllText(_lastRunPath).Trim();
                if (DateTime.TryParse(text, out var dt))
                    return dt;
            }
            catch { }

            return null;
        }

        private void SaveLastRun(DateTime dt)
        {
            try
            {
                File.WriteAllText(_lastRunPath, dt.ToString("o"));
            }
            catch { }
        }
    }
}