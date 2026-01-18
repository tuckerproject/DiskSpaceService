using System;

namespace DiskSpaceService.Models
{
    /// <summary>
    /// Represents the current status of a disk volume, including free space,
    /// total capacity, and percentage available. This model is used by the
    /// DiskAlertMonitor to determine whether an alert should be triggered.
    /// </summary>
    public class DiskStatus
    {
        /// <summary>
        /// The drive letter or mount point (e.g., "C:", "D:", "/mnt/data").
        /// </summary>
        public string DriveName { get; set; } = string.Empty;

        /// <summary>
        /// Total disk capacity in gigabytes.
        /// </summary>
        public double TotalSpaceGb { get; set; }

        /// <summary>
        /// Available free space in gigabytes.
        /// </summary>
        public double FreeSpaceGb { get; set; }

        /// <summary>
        /// Percentage of free space (0–100).
        /// </summary>
        public double PercentFree =>
            TotalSpaceGb > 0
                ? Math.Round((FreeSpaceGb / TotalSpaceGb) * 100, 2)
                : 0;

        /// <summary>
        /// Returns a formatted string for logging or debugging.
        /// </summary>
        public override string ToString()
        {
            return $"{DriveName}: {FreeSpaceGb} GB free of {TotalSpaceGb} GB ({PercentFree}% free)";
        }
    }
}