# StorageWatch

StorageWatch is a self-hosted disk monitoring solution with three components:

- **StorageWatchAgent**: Windows service that monitors local drives, stores metrics in SQLite, and sends alerts.
- **StorageWatchServer**: Central ASP.NET Core server that ingests agent reports and hosts a web dashboard.
- **StorageWatchUI**: WPF desktop app for local monitoring and service control.

## Documentation

All active project documentation is centralized in:

- **[/Docs/README.md](/Docs/README.md)**

## License

CC0 1.0 Universal (Public Domain). See [/LICENSE](/LICENSE).
