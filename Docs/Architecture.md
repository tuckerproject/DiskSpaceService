# Architecture

## Solution Components

StorageWatch is composed of three applications plus an updater:

- **StorageWatchAgent** (`StorageWatchAgent`): .NET worker service (`net10.0`) running as a Windows Service.
- **StorageWatchServer** (`StorageWatchServer`): ASP.NET Core app (`net10.0`) running as a Windows Service.
- **StorageWatchUI** (`StorageWatchUI`): WPF desktop app (`net8.0-windows7.0`).
- **StorageWatch.Updater** (`StorageWatch.Updater`): update handoff/orchestration executable.

## Agent Runtime Flow

1. On startup, the agent ensures `%ProgramData%\StorageWatch\Agent` exists.
2. It creates `%ProgramData%\StorageWatch\Agent\AgentConfig.json` from `Defaults/AgentConfig.default.json` when missing.
3. The `Worker` hosted service initializes SQLite schema and starts:
   - notification loop for threshold/state-based alerting
   - SQL reporting scheduler for periodic local data writes
   - retention cleanup checks
4. In `Agent` mode, `CentralPublisher` batches rows from local `DiskSpaceLog` and posts to server `/api/agent/report`.

## Server Runtime Flow

1. On startup, server uses `%ProgramData%\StorageWatch\Server` (or test temp path in tests).
2. It creates `%ProgramData%\StorageWatch\Server\ServerConfig.json` from `Defaults/ServerConfig.default.json` when missing.
3. It initializes SQLite schema and starts:
   - Razor Pages dashboard (`/`, `/alerts`, `/settings`, `/machines/{id:int}`)
   - API controllers (health, ping, agent ingestion, update endpoints)
   - auto-update worker services
4. Raw agent rows are ingested by `RawRowIngestionService` into `RawDriveRows`.

## UI Runtime Flow

1. UI config loads from local `appsettings.json` plus `%ProgramData%\StorageWatch\Agent\AgentConfig.json`.
2. Main window provides views:
   - Dashboard
   - Trends
   - Settings
   - Service Status
3. Local data is read from `%ProgramData%\StorageWatch\Agent\StorageWatch.db`.
4. Service control actions target Windows service `StorageWatchAgent`.

## Data Storage Locations

- Agent config: `%ProgramData%\StorageWatch\Agent\AgentConfig.json`
- Agent database: `%ProgramData%\StorageWatch\Agent\StorageWatch.db`
- Agent logs: `%ProgramData%\StorageWatch\Agent\Logs\agent.log`
- Server config: `%ProgramData%\StorageWatch\Server\ServerConfig.json`
- Server database: `%ProgramData%\StorageWatch\Server\StorageWatchServer.db`
- Server logs: `%ProgramData%\StorageWatch\Server\Logs\server.log`

## Key Server Database Tables

- `RawDriveRows`: raw rows accepted from agent reports
- `Machines`: machine registry + last seen timestamp
- `MachineDrives`: latest drive state per machine
- `DiskHistory`: historical drive metrics
- `Alerts`: active and historical alert records
- `Settings`: server configuration values shown in dashboard
