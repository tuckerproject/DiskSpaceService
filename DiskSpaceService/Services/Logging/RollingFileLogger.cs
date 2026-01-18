using System;
using System.IO;

namespace DiskSpaceService.Services.Logging
{
    public class RollingFileLogger
    {
        private readonly string _logFilePath;
        private readonly object _lock = new();

        private const long MaxSizeBytes = 1_000_000; // 1 MB
        private const int MaxFiles = 3;

        public RollingFileLogger(string logFilePath)
        {
            _logFilePath = logFilePath;

            string? dir = Path.GetDirectoryName(_logFilePath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
        }

        public void Log(string message)
        {
            lock (_lock)
            {
                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}";
                File.AppendAllText(_logFilePath, line + Environment.NewLine);

                RollIfNeeded();
            }
        }

        private void RollIfNeeded()
        {
            FileInfo fi = new FileInfo(_logFilePath);
            if (!fi.Exists || fi.Length < MaxSizeBytes)
                return;

            // Delete the oldest file (service.log.3)
            string oldest = _logFilePath + ".3";
            if (File.Exists(oldest))
                File.Delete(oldest);

            // Shift .2 → .3
            string two = _logFilePath + ".2";
            if (File.Exists(two))
                File.Move(two, oldest);

            // Shift .1 → .2
            string one = _logFilePath + ".1";
            if (File.Exists(one))
                File.Move(one, two);

            // Shift current → .1
            File.Move(_logFilePath, one, true);

            // Create a new empty service.log
            File.WriteAllText(_logFilePath, string.Empty);
        }
    }
}