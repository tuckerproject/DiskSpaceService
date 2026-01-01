# DiskSpaceService

📄 README.md — Disk Space Monitoring Service using .NET version 8.0

🧭 Overview
The Disk Space Monitoring Service is a lightweight Windows Service designed to:
- Collect disk space metrics from one or more drives
- Store those metrics in a SQL database
- Send real‑time alerts when disk space falls below a configurable threshold
- Notify when disk space returns to normal
- Maintain rolling log files for auditability
- Run reliably with minimal configuration
This project provides a simple, self‑hosted monitoring solution without relying on cloud services or heavy enterprise tools.

🚀 Features
✔ Daily Disk Space Collection
Runs once per day and logs:
- Total size
- Used space
- Free space
- Percent free
- Machine name
- Timestamp
✔ Continuous Alert Monitoring
Checks disk space every minute and:
- Sends a low disk space alert when below threshold
- Sends a normal alert when recovered
- Avoids duplicate alerts using a persistent state file
✔ GroupMe Alert Integration
Alerts are sent to a GroupMe bot for:
- Instant notifications
- Mobile visibility
- Group alerts
✔ Rolling Log Files
Logs are stored in:
C:\ProgramData\DiskSpaceService\Logs

The logger:
- Rotates at 1 MB
- Keeps the last 3 logs
- Ensures clean audit history
✔ XML‑Based Configuration
All settings are stored in a simple XML file that users can edit without recompiling.

📦 Installation
1. Clone the repository
git clone https://github.com/tuckerproject/DiskSpaceService
2. Build the project
Open the solution in Visual Studio and build in Release mode.
3. Install as a Windows Service
Run PowerShell as Administrator:
sc create DiskSpaceService binPath= "C:\Path\To\Your\Executable.exe"
sc start DiskSpaceService

⚙ Configuration
The configuration file is:
DiskSpaceConfig.xml
This file is not included in the repository for security reasons.
Instead, the repo includes:
DiskSpaceConfig.example.xml
Copy it and rename:
DiskSpaceConfig.xml
Then edit the values as needed.

📝 Example Configuration
```xml
<DiskSpaceServiceConfig>
  <CollectionTime>08:00</CollectionTime>
  <RunMissedCollection>true</RunMissedCollection>

  <Drives>
    <Drive>C</Drive>
    <Drive>D</Drive>
  </Drives>

  <Database>
    <ConnectionString>YOUR_CONNECTION_STRING_HERE</ConnectionString>
  </Database>

  <Alert>
    <ThresholdPercent>10</ThresholdPercent>
    <GroupMeBotId>YOUR_GROUPME_BOT_ID_HERE</GroupMeBotId>
  </Alert>
</DiskSpaceServiceConfig>
```

🔧 Configuration Details
CollectionTime
Daily run time in 24‑hour format.
RunMissedCollection
If true, the service runs immediately after boot if the scheduled time was missed.
Drives
List of drive letters to monitor.
Database.ConnectionString
Your SQL Server connection string.
Alert.ThresholdPercent
Triggers alerts when free space drops below this percentage.
Alert.GroupMeBotId
Your GroupMe bot ID (keep this secret).

📊 Database Schema
CREATE TABLE DiskSpaceMetrics (
    Id INT IDENTITY PRIMARY KEY,
    MachineName NVARCHAR(100),
    DriveLetter NVARCHAR(10),
    TotalSpaceGB DECIMAL(10,2),
    UsedSpaceGB DECIMAL(10,2),
    FreeSpaceGB DECIMAL(10,2),
    PercentFree DECIMAL(5,2),
    TimestampUtc DATETIME
);

🔔 Alerts
Low Disk Space Alert
Triggered when:
PercentFree < ThresholdPercent

Disk Space Normal Alert
Triggered when:
PercentFree >= ThresholdPercent

Alert State Persistence
Stored in:
AlertState.json

This prevents duplicate alerts and ensures correct behavior across restarts.

🧱 Architecture Overview
- Worker Service — main loop
- DailyRunScheduler — daily SQL logging
- DiskSpaceAlertMonitor — continuous alerting
- GroupMeAlertService — sends GroupMe messages
- RollingFileLogger — log rotation
- AlertStateStore — persists alert state
- DiskSpaceCollector — reads disk metrics

🤝 Contributing
Contributions are welcome!
Feel free to fork the project, create feature branches, and submit pull requests.

📜 License
This project is licensed under the MIT License.
