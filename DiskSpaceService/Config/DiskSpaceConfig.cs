using System;
using System.Collections.Generic;

namespace DiskSpaceService.Config
{
    public class DiskSpaceConfig
    {
        public bool EnableSqlReporting { get; set; }
        public bool RunMissedCollection { get; set; }
        public bool RunOnlyOncePerDay { get; set; }
        public TimeSpan CollectionTime { get; set; }

        public bool EnableNotifications { get; set; }

        public List<string> Drives { get; set; } = new();
        public int ThresholdPercent { get; set; }

        public DatabaseConfig Database { get; set; } = new();

        public GroupMeConfig GroupMe { get; set; } = new();
        public SmtpConfig Smtp { get; set; } = new();

        public bool EnableStartupLogging { get; set; }
    }

    public class DatabaseConfig
    {
        public string ConnectionString { get; set; } = string.Empty;
    }
}