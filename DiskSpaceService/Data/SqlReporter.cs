using DiskSpaceService.Config;
using DiskSpaceService.Models;
using DiskSpaceService.Services.Logging;
using Microsoft.Data.SqlClient;
using System;
using System.IO;
using System.Threading.Tasks;

namespace DiskSpaceService.Services.Scheduling
{
    public class SqlReporter
    {
        private readonly DiskSpaceConfig _config;
        private readonly RollingFileLogger _logger;

        public SqlReporter(DiskSpaceConfig config, RollingFileLogger logger)
        {
            _config = config;
            _logger = logger;
        }

        public async Task WriteDailyReportAsync()
        {
            try
            {
                using var connection = new SqlConnection(_config.Database.ConnectionString);
                await connection.OpenAsync();

                string machineName = Environment.MachineName;

                foreach (var driveLetter in _config.Drives)
                {
                    var status = GetDiskStatus(driveLetter);

                    // -----------------------------------------
                    // Skip SQL insert if drive is NOT READY
                    // -----------------------------------------
                    if (status.TotalSpaceGb == 0)
                    {
                        _logger.Log($"[SQL] Skipping insert for drive {status.DriveName}: drive not ready.");
                        continue;
                    }

                    string sql = @"
                        INSERT INTO DiskSpaceLog
                        (MachineName, DriveLetter, TotalSpaceGB, UsedSpaceGB, FreeSpaceGB, PercentFree, CollectionTimeUtc)
                        VALUES (@machine, @drive, @total, @used, @free, @percent, @utc)
                    ";

                    using var command = new SqlCommand(sql, connection);
                    command.Parameters.AddWithValue("@machine", machineName);
                    command.Parameters.AddWithValue("@drive", status.DriveName);
                    command.Parameters.AddWithValue("@total", status.TotalSpaceGb);
                    command.Parameters.AddWithValue("@used", status.TotalSpaceGb - status.FreeSpaceGb);
                    command.Parameters.AddWithValue("@free", status.FreeSpaceGb);
                    command.Parameters.AddWithValue("@percent", status.PercentFree);
                    command.Parameters.AddWithValue("@utc", DateTime.UtcNow);

                    try
                    {
                        await command.ExecuteNonQueryAsync();
                        _logger.Log(
                            $"[SQL] Inserted row for drive {status.DriveName}: Free {status.FreeSpaceGb:F2} GB ({status.PercentFree:F2}%)."
                        );
                    }
                    catch (Exception ex)
                    {
                        _logger.Log(
                            $"[SQL ERROR] Failed to insert row for drive {status.DriveName}: {ex}"
                        );
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Log($"[SQL ERROR] Connection or execution failure: {ex}");
            }
        }

        private DiskStatus GetDiskStatus(string driveLetter)
        {
            try
            {
                var di = new DriveInfo(driveLetter);

                if (!di.IsReady)
                {
                    _logger.Log($"[SQL] Drive {driveLetter} is not ready.");
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
            catch (Exception ex)
            {
                _logger.Log($"[SQL] Error reading drive {driveLetter}: {ex}");

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