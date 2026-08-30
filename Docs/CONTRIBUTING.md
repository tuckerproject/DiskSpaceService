# Contributing

## Prerequisites

- .NET SDK supporting all project target frameworks
- Git
- Windows for service and WPF development paths

## Build

```powershell
dotnet build StorageWatch.slnx
```

## Test

```powershell
dotnet test StorageWatch.slnx
```

## Documentation Policy

- Keep canonical docs under `/Docs`.
- Remove obsolete historical documentation rather than preserving duplicates.
- Update documentation when behavior or configuration changes.

## Pull Requests

Before opening a pull request:

- ensure build succeeds
- ensure tests pass
- update `/Docs` as needed
- avoid committing secrets
