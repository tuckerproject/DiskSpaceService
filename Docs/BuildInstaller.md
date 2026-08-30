# Build Installer

## Prerequisites

- .NET SDK supporting project target frameworks
- NSIS (`makensis.exe`)
- Successful publish outputs for Agent, Server, UI, and Updater

## Publish Commands

From repository root:

```powershell
dotnet publish StorageWatchAgent\StorageWatchAgent.csproj -c Release -f net10.0 -o InstallerNSIS\Payload\Agent
dotnet publish StorageWatchServer\StorageWatchServer.csproj -c Release -f net10.0 -o InstallerNSIS\Payload\Server
dotnet publish StorageWatchUI\StorageWatchUI.csproj -c Release -f net8.0-windows7.0 -o InstallerNSIS\Payload\UI
dotnet publish StorageWatch.Updater\StorageWatch.Updater.csproj -c Release -f net10.0 -r win-x64 --self-contained false -o InstallerNSIS\Payload\Updater
```

Ensure payload defaults are present:

- `InstallerNSIS/Payload/Agent/Defaults/AgentConfig.default.json`
- `InstallerNSIS/Payload/Server/Defaults/ServerConfig.default.json`

## Build NSIS Package

```powershell
makensis InstallerNSIS\StorageWatchInstaller.nsi
```

Output:

- `StorageWatchInstaller.exe`
