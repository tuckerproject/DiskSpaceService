# Installer Guide

StorageWatch uses an NSIS installer (`StorageWatchInstaller.exe`) for Windows deployment.

## What the Installer Deploys

- Agent binaries to `$INSTDIR\Agent`
- Server binaries to `$INSTDIR\Server`
- UI binaries to `$INSTDIR\UI`
- Updater binaries to `$INSTDIR\Updater`

## Windows Services

- Agent service name: `StorageWatchAgent`
- Server service name: `StorageWatchServer`

## ProgramData Layout

Installer and runtime components use `%ProgramData%\StorageWatch` with split folders:

- `%ProgramData%\StorageWatch\Agent`
- `%ProgramData%\StorageWatch\Server`
- `%ProgramData%\StorageWatch\Logs`
- `%ProgramData%\StorageWatch\Plugins`

On first run, component defaults are copied to:

- `%ProgramData%\StorageWatch\Agent\AgentConfig.json`
- `%ProgramData%\StorageWatch\Server\ServerConfig.json`

## Start Menu

Installer creates shortcuts including:

- `StorageWatch Dashboard`
- `StorageWatch Central Dashboard`
- `StorageWatch Logs`

## Uninstall

Uninstall removes installed binaries and services. ProgramData content is managed by uninstall prompts and may be preserved unless explicitly removed.
