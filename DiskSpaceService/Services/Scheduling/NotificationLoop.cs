using DiskSpaceService.Config;
using DiskSpaceService.Models;
using DiskSpaceService.Services.Alerting;
using DiskSpaceService.Services.Logging;
using DiskSpaceService.Services.Monitoring;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DiskSpaceService.Services.Scheduling
{
    public class NotificationLoop
    {
        private readonly DiskSpaceConfig _config;
        private readonly List<IAlertSender> _senders;
        private readonly DiskAlertMonitor _monitor;
        private readonly RollingFileLogger _logger;

        // Tracks last known state per drive: NORMAL, ALERT, NOT_READY
        private readonly Dictionary<string, string> _lastAlertState = new();

        public NotificationLoop(
            DiskSpaceConfig config,
            List<IAlertSender> senders,
            DiskAlertMonitor monitor,
            RollingFileLogger logger)
        {
            _config = config;
            _senders = senders;
            _monitor = monitor;
            _logger = logger;
        }

        public async Task RunAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    foreach (var driveLetter in _config.Drives)
                    {
                        var status = _monitor.GetStatus(driveLetter);

                        string state;
                        string message;

                        // -----------------------------
                        // 1. Drive NOT READY
                        // -----------------------------
                        if (status.TotalSpaceGb == 0)
                        {
                            state = "NOT_READY";
                            message = $"ALERT: Drive {status.DriveName} is NOT READY or unavailable.";
                        }
                        else
                        {
                            // -----------------------------
                            // 2. Drive READY — check threshold
                            // -----------------------------
                            bool belowThreshold = status.PercentFree < _config.ThresholdPercent;

                            if (belowThreshold)
                            {
                                state = "ALERT";
                                message =
                                    $"ALERT: Drive {status.DriveName} is below threshold. " +
                                    $"{status.FreeSpaceGb:F2} GB free ({status.PercentFree:F2}%).";
                            }
                            else
                            {
                                state = "NORMAL";
                                message =
                                    $"RECOVERY: Drive {status.DriveName} has recovered. " +
                                    $"{status.FreeSpaceGb:F2} GB free ({status.PercentFree:F2}%).";
                            }
                        }

                        // -----------------------------
                        // 3. Compare with last state
                        // -----------------------------
                        _lastAlertState.TryGetValue(driveLetter, out var lastState);

                        if (lastState != state)
                        {
                            // Log transition
                            if (state == "NOT_READY")
                                _logger.Log($"[ALERT] Drive {status.DriveName} NOT READY.");
                            else if (state == "ALERT")
                                _logger.Log($"[ALERT] Drive {status.DriveName} LOW SPACE ({status.PercentFree:F2}%).");
                            else if (state == "NORMAL")
                                _logger.Log($"[ALERT] Drive {status.DriveName} RECOVERED ({status.PercentFree:F2}%).");

                            // Send alert
                            foreach (var sender in _senders)
                                await sender.SendAlertAsync(message);

                            // Update state
                            _lastAlertState[driveLetter] = state;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Log($"[ALERT] Notification loop error: {ex}");
                }

                await Task.Delay(TimeSpan.FromMinutes(1), token);
            }
        }
    }
}