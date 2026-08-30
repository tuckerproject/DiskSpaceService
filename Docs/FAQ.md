# FAQ

## What is StorageWatch?

StorageWatch is a self-hosted disk monitoring platform with agent, server, and desktop UI components.

## Is the server required?

No. Agent + UI can run standalone without central aggregation.

## Where are runtime configs stored?

- Agent: `%ProgramData%\StorageWatch\Agent\AgentConfig.json`
- Server: `%ProgramData%\StorageWatch\Server\ServerConfig.json`

## Where is local disk history stored?

`%ProgramData%\StorageWatch\Agent\StorageWatch.db`

## What API does the agent use to report to server?

`POST /api/agent/report`

## Which dashboards are available on server?

- `/`
- `/alerts`
- `/settings`
- `/machines/{id:int}`

## Which views are available in the desktop UI?

- Dashboard
- Trends
- Settings
- Service Status
