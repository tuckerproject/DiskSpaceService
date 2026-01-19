# DiskSpaceService

A lightweight, self‑hosted Windows Service for monitoring disk space, logging metrics to SQL, and sending real‑time alerts through GroupMe or SMTP. Built on .NET 8.0 and designed for reliability, clarity, and minimal configuration.


## 🧭 Overview

DiskSpaceService continuously monitors one or more drives and provides:

- Real‑time alerts when disk space falls below a configurable threshold
- Recovery notifications when space returns to normal
- Daily SQL logging of disk metrics
- Rolling log files for auditability
- A clean, state‑driven architecture that avoids duplicate alerts
- Support for GroupMe and SMTP alerting
- A simple XML configuration file

This service is ideal for home labs, small servers, or any environment where lightweight, dependable monitoring is needed.


## 🚀 Features

### ✔ Continuous Alert Monitoring

Runs every minute and uses a state machine to detect:

- ALERT — Drive below threshold
- NORMAL — Drive healthy
- NOT_READY — Drive unavailable or unmounted

Alerts are only sent when the state changes.


### ✔ Machine‑Name‑Prefixed Alerts

All alerts include the machine name, making multi‑machine monitoring easy.


### ✔ Network‑Ready Alerting

Alerts are delayed until DNS resolution succeeds, preventing startup failures.


### ✔ State File Persistence

Alert state is stored in:

C:\ProgramData\DiskSpaceService\alert_state.json

This prevents reboot spam and ensures correct behavior across restarts.


### ✔ Daily SQL Reporting

Once per day, the service logs:

- Total space
- Used space
- Free space
- Percent free
- Drive letter
- Machine name
- Timestamp

Missed runs (e.g., due to reboot) are automatically recovered.


### ✔ Rolling Log Files

Logs are stored in:

C:\ProgramData\DiskSpaceService\Logs

- Rotates at 1 MB
- Keeps the last 3 logs
- Ensures clean audit history


### ✔ GroupMe & SMTP Alerts

Choose one or both:

- GroupMe bot messages
- SMTP email alerts


## 📦 Installation

1. Clone the repository:

git clone https://github.com/tuckerproject/DiskSpaceService

2. Build the project  
Open the solution in Visual Studio and build in Release mode.

3. Install as a Windows Service  
Run PowerShell as Administrator:

sc create DiskSpaceService binPath= "C:\Path\To\Your\Executable.exe"
sc start DiskSpaceService


## ⚙ Configuration

The configuration file is:

DiskSpaceConfig.xml

This file is not included in the repository for security reasons.

Instead, the repo includes:

DiskSpaceConfig.example.xml

Copy it and rename:

DiskSpaceConfig.xml

Then edit the values as needed.


## 📝 Example Configuration (v2.0)

```xml
<DiskSpaceConfig>

  <!-- SQL Reporting -->
  <EnableSqlReporting>true</EnableSqlReporting>
  <RunMissedCollection>true</RunMissedCollection>
  <RunOnlyOncePerDay>true</RunOnlyOncePerDay>
  <CollectionTime>08:00</CollectionTime>

  <!-- Disk Monitoring -->
  <ThresholdPercent>10</ThresholdPercent>

  <Drives>
    <Drive>C</Drive>
    <Drive>D</Drive>
  </Drives>

  <!-- Database -->
  <Database>
    <ConnectionString>
      Server=.;Database=DiskReports;Trusted_Connection=True;TrustServerCertificate=True;
    </ConnectionString>
  </Database>

  <!-- GroupMe Alerts -->
  <GroupMe>
    <Enabled>true</Enabled>
    <BotId>YOUR_BOT_ID</BotId>
  </GroupMe>

  <!-- SMTP Alerts -->
  <Smtp>
    <Enabled>false</Enabled>
    <Host>smtp.example.com</Host>
    <Port>587</Port>
    <UseSsl>true</UseSsl>
    <Username>youruser</Username>
    <Password>yourpassword</Password>
    <FromAddress>alerts@example.com</FromAddress>
    <ToAddress>you@example.com</ToAddress>
  </Smtp>

</DiskSpaceConfig>
```


## 🔧 Configuration Details

### SQL Reporting

- EnableSqlReporting — Enables daily SQL logging
- RunMissedCollection — Runs immediately after boot if the scheduled time was missed
- RunOnlyOncePerDay — Ensures only one run per day
- CollectionTime — Daily run time (24‑hour format)


### Disk Monitoring

- ThresholdPercent — Alerts when free space drops below this percentage
- Drives — List of drive letters to monitor


### Database

- ConnectionString — SQL Server connection string


### GroupMe Alerts

- Enabled — Enables GroupMe alerts
- BotId — Your GroupMe bot ID


### SMTP Alerts

- Enabled — Enables SMTP alerts
- Host / Port / UseSsl — SMTP server settings
- Username / Password — SMTP credentials
- FromAddress / ToAddress — Email sender and recipient


## 📊 Database Schema

```sql
CREATE TABLE DiskSpaceLog (
    Id INT IDENTITY PRIMARY KEY,
    MachineName NVARCHAR(100),
    DriveLetter NVARCHAR(10),
    TotalSpaceGB DECIMAL(10,2),
    UsedSpaceGB DECIMAL(10,2),
    FreeSpaceGB DECIMAL(10,2),
    PercentFree DECIMAL(5,2),
    CollectionTimeUtc DATETIME
);
```


## 🔔 Alerts (v2.0)

### Alert States

- NORMAL — Drive is healthy
- ALERT — Drive below threshold
- NOT_READY — Drive unavailable or unmounted


### Alert Behavior

- Alerts are sent only when the state changes
- Recovery alerts are sent when returning to NORMAL
- All alerts include the machine name
- Alerts are delayed until the network is ready
- State is persisted to avoid duplicate alerts


### State File

C:\ProgramData\DiskSpaceService\alert_state.json


## 🧱 Architecture Overview (v2.0)

- Worker Service — Hosts background loops
- NotificationLoop — Continuous alert monitoring with state machine
- SqlReporter — Daily SQL logging with missed‑run recovery
- DiskAlertMonitor — Reads disk metrics and drive readiness
- AlertSenderFactory — Creates enabled alert senders
- GroupMeAlertSender — Sends GroupMe messages
- SmtpAlertSender — Sends email alerts
- RollingFileLogger — Log rotation and audit history
- State File — Persists last alert state


## 🤝 Contributing

Contributions are welcome.  
Feel free to fork the project, create feature branches, and submit pull requests.


## 📜 License

This project is licensed under the MIT License.