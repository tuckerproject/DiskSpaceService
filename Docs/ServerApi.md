# Server API Reference

Base URL is the configured `Server.ListenUrl`.

## Health and Ping

### `GET /api/health`
Returns basic health status.

### `GET /api/server/ping`
Returns `pong` for simple connectivity tests.

## Agent Ingestion

### `POST /api/agent/report`
Accepts batched raw drive rows from an agent.

Request body:

```json
{
  "machineName": "MACHINE-01",
  "rows": [
    {
      "driveLetter": "C:",
      "totalSpaceGb": 500,
      "usedSpaceGb": 300,
      "freeSpaceGb": 200,
      "percentFree": 40,
      "timestamp": "2026-01-01T12:00:00Z"
    }
  ]
}
```

Validation rules:

- `machineName` is required
- `rows` must be non-null and non-empty
- each row must include `driveLetter`

## Update Endpoints

### `GET /api/update/status`
Returns current/latest versions and install state.

### `POST /api/update/install`
Triggers update install flow.

### `POST /api/update/restart`
Requests restart behavior through update/restart handler.
