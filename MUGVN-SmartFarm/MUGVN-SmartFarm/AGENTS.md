# Project guidance

This file applies to this repository and its subdirectories. Run commands below
from this directory, which contains `MUGVN-SmartFarm.sln`. Read `README.md` for
setup and `VERIFICATION.md` for recorded evidence and limitations; verify current
source and runtime state before treating past results as current facts.

## Structure and responsibilities

- `MUGVN-SmartFarm.csproj`, `Program.cs`, `Api/`, `Application/`, `Middleware/`, `Realtime/`:
  .NET 10 ASP.NET Core API using FastEndpoints, Keycloak JWT authentication,
  Problem Details, OpenAPI, and SignalR. Application services own device operations
  and telemetry queries; API endpoints own HTTP requests and responses.
- `Ingestion/`: separate .NET worker receiving MQTT, validating registered
  sources, parsing MQTT payloads, persisting telemetry, and publishing Redis notifications.
- `Contracts/`: focused device, telemetry, history, and storage contracts shared by
  the API and worker. Interfaces live with the feature they describe.
- `Infrastructure/`: MongoDB persistence and Redis connection/publishing code.
  `Infrastructure/keycloak/` holds the local realm import.
- `UI/`: JavaScript source, state merging, Node tests, and end-to-end harness.
  `UI/app.js` bundles to `wwwroot/demo/app.js`; edit source and rebuild the bundle.
  Demo HTML and CSS live in `wwwroot/demo/`.
- `Tests/`: xUnit tests, API tests, and opt-in MongoDB integration tests.
- `Infrastructure/keycloak/`, `scripts/initialize-keycloak.sh`, `compose.yaml`,
  `datadog/`: local identity, full Compose deployment, and optional Datadog Agent.

Keep changes within the responsible project. Preserve project references and the
API project's exclusions for `Contracts/`, `Infrastructure/`, `Ingestion/`, `Tests/`, and `UI/`; these
directories must not be compiled into the API through default file discovery.
Follow nearby C# conventions: nullable reference types, implicit usings,
constructor injection, asynchronous I/O, and cancellation propagation. Reuse
existing abstractions and injected `TimeProvider` where appropriate.

## Local setup and commands

Prerequisites: .NET 10 SDK, Node/npm, and reachable MongoDB, EMQX, and Redis.
Development defaults are MongoDB port 27018, MQTT 1883, Redis 6379, and Keycloak 8085.
API and worker have independent `appsettings.json` files; use .NET environment
overrides such as `Mongo__ConnectionString`, or development user secrets.
Local service addresses are in each host's `appsettings.Development.json`.
Production requires explicit MongoDB and Redis addresses, MQTT host for ingestion,
and Keycloak authority for the API. Worker configuration loads from its output directory.

```bash
# Install frontend dependencies and generate the browser bundle.
npm --prefix UI ci
npm --prefix UI run build
dotnet restore MUGVN-SmartFarm.sln
dotnet build MUGVN-SmartFarm.sln

# Local identity setup: prepare .env from .env.example with real local passwords.
docker compose up -d keycloak
# Wait for http://localhost:8085/realms/smartfarm/.well-known/openid-configuration.
./scripts/initialize-keycloak.sh

# Run these in separate terminals after infrastructure is ready.
dotnet run --project MUGVN-SmartFarm.csproj --launch-profile https
dotnet run --project Ingestion/SmartFarm.Ingestion.csproj
```

Development URLs: `https://localhost:7017/demo/`, `/swagger/`, and
`/openapi/v1.json`. The HTTPS profile also binds HTTP port 5198. OpenAPI and
Swagger are development-only. See `README.md` for certificate and login setup.
The full Compose stack instead serves the Svelte UI at `http://localhost:5173/`
and the API demo at `http://localhost:5198/demo/`. Its services reuse existing
external MongoDB, EMQX, Redis, and Keycloak volumes. Follow the README cutover
and rollback steps; never attach two running containers to those volumes.

## Contracts to preserve

- Data flow: Pi MQTT → ingestion → MongoDB → best-effort Redis → API SignalR
  → browser. MongoDB is the durable source of truth; Redis is not a durable queue.
- Producer topics are `mugvn/farm/{device_id}/env` and `/avail`. Validate topic
  identity against payload identity and registration. Preserve the Python
  producer contract; do not add remote command topics or reset replay state as
  incidental backend work.
- Default seeded mappings are pi4-1/node6, pi4-2/node18, pi4-3/node24, and
  pi4-4/node38. Unknown, disabled, decommissioned, or mismatched sources are rejected.
- Keep measurement time, emission time, and ingestion time distinct. Producer
  `timestamp` maps to BSON UTC `timeStamp` and API `source_timestamp`.
  HTTP, Redis, and SignalR use snake_case through shared serialization settings.
- The default `SmartFarm` collection is MongoDB Time Series with time field
  `timeStamp` and metadata `sensor`. Preserve existing data and reject incompatible
  collections; never drop or recreate them to make startup succeed.
- Run one ingestion instance. Deduplication is application-level by device and
  canonical `message_id`; Time Series has no unique message-ID constraint.
  Preserve the original ingestion time on duplicates and recovery. Acknowledge
  accepted MQTT telemetry after database work; invalid sources are rejected and
  acknowledged. Redis failure must not undo successful persistence.
- Latest readings sort by source time, emission time, then message ID. Late data
  and replay resets must not replace newer device cards. Keep snapshot/live merge,
  reconnect reconciliation, and independent device updates intact.
- Connectivity and freshness are separate. Freshness uses stored ingestion time;
  retained MQTT availability alone does not establish healthy sensor reporting.
- History uses `[from, to)`, at most 366 days, pages of 1–1000 items, and cursors
  bound to device and time range. UTC hour/day averages exclude missing
  observations and include available anomalous readings; preserve quality counts.
- Device PATCH changes label, interval, or enabled state. DELETE decommissions
  without deleting history or freeing the source identity.
- API data endpoints and `/hubs/telemetry` require the `FarmAdmin` policy
  (default Keycloak audience `smartfarm-api`, realm role `farm-admin`). Keep JWT
  issuer, signature, audience, lifetime, and role validation. Query-string access
  tokens are accepted only on the hub route. Browser login uses PKCE and keeps
  tokens in memory. Preserve token-safe logging and production HTTPS metadata.

## Validation and change hygiene

```bash
dotnet test MUGVN-SmartFarm.sln
npm --prefix UI test
git diff --check
```

Use checks appropriate to the change. Rebuild the frontend bundle after changing
its source. MongoDB integration tests skip unless `SMARTFARM_TEST_MONGO_URI` is
set; use only an instance where creating and dropping isolated `smartfarm_test_*`
databases is appropriate. End-to-end verification is
`npm --prefix UI run test:e2e` after building the solution and frontend. It needs
initialized Keycloak, infrastructure, Node, and `mongosh`, and ports 5198/7017
free; inspect running processes before stopping anything. See `README.md` for
the harness's isolation and temporary certificate handling.

Preserve unrelated staged, unstaged, and untracked work. Keep credentials,
`.env`, tokens, user secrets, build outputs, and dependency directories out of
commits and logs. Use `.env.example` for placeholders. Do not reset Pi SQLite
files, delete production data, or alter existing containers as part of routine
verification. Update README and request examples when changing public behavior.
Report checks actually performed, skips, and remaining limitations; previous
verification records and source inspection do not prove a current runtime result.
