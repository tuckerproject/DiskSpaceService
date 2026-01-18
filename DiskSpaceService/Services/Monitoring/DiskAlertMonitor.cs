using DiskSpaceService.Config;
using DiskSpaceService.Models;
using System;
using System.IO;

namespace DiskSpaceService.Services.Monitoring
{
    /// <summary>
    /// Pure disk status provider. 
    /// No logging, no threshold logic, no state tracking.
    /// NotificationLoop handles all alerting and logging.
    /// </summary>
    public class DiskAlertMonitor
    {
        private readonly DiskSpaceConfig _config;

        public DiskAlertMonitor(DiskSpaceConfig config)
        {
            _config = config;
        }

        public DiskStatus GetStatus(string driveLetter)
        {
            try
            {
                var di = new DriveInfo(driveLetter);

                if (!di.IsReady)
                {
                    return new DiskStatus
                    {
                        DriveName = driveLetter,
                        TotalSpaceGb = 0,
                        FreeSpaceGb = 0
                    };
                }

                double totalGb = di.TotalSize / 1024d / 1024d / 1024d;
                double freeGb = di.AvailableFreeSpace / 1024d / 1024d / 1024d;

                return new DiskStatus
                {
                    DriveName = driveLetter,
                    TotalSpaceGb = totalGb,
                    FreeSpaceGb = freeGb
                };
            }
            catch
            {
                return new DiskStatus
                {
                    DriveName = driveLetter,
                    TotalSpaceGb = 0,
                    FreeSpaceGb = 0
                };
            }
        }
    }
}