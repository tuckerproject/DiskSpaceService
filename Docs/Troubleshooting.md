# Troubleshooting

## Agent

### Agent service will not start

1. Verify `StorageWatchAgent` exists and status:
   ```powershell
   Get-Service StorageWatchAgent
   ```
2. Check `%ProgramData%\StorageWatch\Agent\AgentConfig.json` for valid JSON.
3. Check logs in `%ProgramData%\StorageWatch\Agent\Logs`.

### No local data in UI

1. Confirm `%ProgramData%\StorageWatch\Agent\StorageWatch.db` exists.
2. Verify `DiskSpaceLog` has rows for current machine name.
3. Check agent logs for SQLite write errors.

### Agent does not report to server

1. Set `StorageWatch.Mode` to `Agent`.
2. Set `StorageWatch.CentralServer.ServerUrl` to the server base URL.
3. Verify server health endpoint:
   ```powershell
   Invoke-WebRequest http://<server>:5001/api/health
   ```

## Server

### Server does not start

1. Validate `%ProgramData%\StorageWatch\Server\ServerConfig.json`.
2. Confirm configured `ListenUrl` port is free.
3. Check `%ProgramData%\StorageWatch\Server\Logs`.

### Dashboard shows no machines

1. Confirm agents are posting to `POST /api/agent/report`.
2. Inspect `RawDriveRows` and `Machines` tables in server SQLite DB.
3. Verify server clock and agent clocks are not significantly skewed.

## UI

### UI cannot control service

1. Run UI elevated (administrator) when performing service control.
2. Confirm service name is exactly `StorageWatchAgent`.

### UI starts but has empty trends

1. Check selected drive has historical rows in `DiskSpaceLog`.
2. Increase time range in Trends view.

## Installer

### Installer build fails

1. Verify all publish outputs exist under `InstallerNSIS/Payload`.
2. Verify `makensis` is installed and available.
3. Run `makensis InstallerNSIS\StorageWatchInstaller.nsi` from repository root.
