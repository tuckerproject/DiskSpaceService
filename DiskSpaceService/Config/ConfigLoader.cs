using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;

namespace DiskSpaceService.Config
{
    public static class ConfigLoader
    {
        public static DiskSpaceConfig Load(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"Configuration file not found: {path}");

            var xml = XDocument.Load(path);
            var root = xml.Root ?? throw new Exception("Invalid configuration file.");

            var config = new DiskSpaceConfig
            {
                // SQL Reporting
                EnableSqlReporting = ReadBool(root, "EnableSqlReporting"),
                RunMissedCollection = ReadBool(root, "RunMissedCollection"),
                RunOnlyOncePerDay = ReadBool(root, "RunOnlyOncePerDay"),
                CollectionTime = ReadTimeSpan(root, "CollectionTime"),

                // Notifications
                EnableNotifications = ReadBool(root, "EnableNotifications"),

                // Disk Monitoring
                ThresholdPercent = ReadInt(root, "ThresholdPercent"),
                Drives = ReadList(root.Element("Drives"), "Drive"),

                // Database
                Database = new DatabaseConfig
                {
                    ConnectionString = ReadString(root.Element("Database"), "ConnectionString")
                },

                // GroupMe
                GroupMe = new GroupMeConfig
                {
                    EnableGroupMe = ReadBool(root.Element("GroupMe"), "EnableGroupMe"),
                    BotId = ReadString(root.Element("GroupMe"), "BotId")
                },

                // SMTP
                Smtp = new SmtpConfig
                {
                    EnableSmtp = ReadBool(root.Element("Smtp"), "EnableSmtp"),
                    Host = ReadString(root.Element("Smtp"), "Host"),
                    Port = ReadInt(root.Element("Smtp"), "Port"),
                    UseSsl = ReadBool(root.Element("Smtp"), "UseSsl"),
                    Username = ReadString(root.Element("Smtp"), "Username"),
                    Password = ReadString(root.Element("Smtp"), "Password"),
                    FromAddress = ReadString(root.Element("Smtp"), "FromAddress"),
                    ToAddress = ReadString(root.Element("Smtp"), "ToAddress")
                }
            };

            return config;
        }

        // ------------------------------
        // Helper XML parsing methods
        // ------------------------------

        private static bool ReadBool(XElement? parent, string name)
            => bool.Parse(parent?.Element(name)?.Value ?? "false");

        private static int ReadInt(XElement? parent, string name)
            => int.Parse(parent?.Element(name)?.Value ?? "0");

        private static string ReadString(XElement? parent, string name)
            => parent?.Element(name)?.Value ?? string.Empty;

        private static TimeSpan ReadTimeSpan(XElement parent, string name)
            => TimeSpan.Parse(parent.Element(name)?.Value ?? "00:00");

        private static List<string> ReadList(XElement? parent, string elementName)
        {
            var list = new List<string>();
            if (parent == null) return list;

            foreach (var el in parent.Elements(elementName))
                list.Add(el.Value);

            return list;
        }
    }
}