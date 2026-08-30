# Configuration Reference

## StorageWatchAgent Configuration

### Primary file

`%ProgramData%\StorageWatch\Agent\AgentConfig.json`

### Top-level section

`StorageWatch`

### Options

#### `Mode`

- `Standalone` (default)
- `Agent`

#### `General`

- `EnableStartupLogging` (bool)

#### `Monitoring`

- `ThresholdPercent` (int, 1-100)
- `Drives` (string list, at least one drive)

#### `Database`

- `ConnectionString` (string, SQLite)

#### `Alerting`

- `EnableNotifications` (bool)
- `Plugins` (dictionary of plugin-specific config)
- `Smtp` (legacy-compatible SMTP config)
- `GroupMe` (legacy-compatible GroupMe config)

`Smtp` fields:
- `Enabled`, `Host`, `Port`, `UseSsl`, `Username`, `Password`, `FromAddress`, `ToAddress`

`GroupMe` fields:
- `Enabled`, `BotId`

#### `CentralServer`

- `ServerUrl` (string)
- `CheckIntervalSeconds` (int)
- `ApiKey` (string)

#### `SqlReporting`

- `Enabled` (bool)
- `RunMissedCollection` (bool)
- `RunOnlyOncePerDay` (bool)
- `CollectionTime` (string, `HH:mm`)

#### `Retention`

- `Enabled` (bool)
- `MaxDays` (int)
- `MaxRows` (int)
- `CleanupIntervalMinutes` (int)
- `ArchiveEnabled` (bool)
- `ArchiveDirectory` (string)
- `ExportCsvEnabled` (bool)

#### `AutoUpdate`

- `Enabled` (bool)
- `ManifestUrl` (string)
- `CheckIntervalMinutes` (int)

## StorageWatchServer Configuration

### Primary file

`%ProgramData%\StorageWatch\Server\ServerConfig.json`

### Sections

#### `Server`

- `ListenUrl` (default `http://localhost:5001`)
- `DatabasePath` (default `%ProgramData%\StorageWatch\Server\StorageWatchServer.db`)
- `OnlineTimeoutMinutes` (default `10`)

#### `AutoUpdate`

- `Enabled` (bool)
- `ManifestUrl` (string)
- `UseAgentDrivenUpdates` (bool)

## StorageWatchUI Configuration

### File

`StorageWatchUI/appsettings.json` (copied with app)

### Sections

#### `StorageWatchUI`

- `RefreshIntervalSeconds`
- `ChartDataPoints`
- `Theme`

#### `AutoUpdate`

- `Enabled`
- `ManifestUrl`

## JSON Schemas

- `ConfigSchemas/AgentConfig.schema.json`
- `ConfigSchemas/ServerConfig.schema.json`
