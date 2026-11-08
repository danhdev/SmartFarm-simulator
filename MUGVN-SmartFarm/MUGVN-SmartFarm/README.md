# SmartFarm

Two .NET 10 processes: MQTT ingestion writes MongoDB Time Series and publishes best-effort Redis notifications; the API serves authenticated queries and forwards notifications through SignalR. The demo updates nodes independently. Keycloak owns passwords, sessions, token issuance, and refresh.

## Full local Compose stack

`compose.yaml` runs MongoDB Atlas Local, EMQX, Redis, Keycloak, the API, one ingestion worker, and the separate Svelte/Nginx frontend. The four QEMU Pis stay outside Compose and continue publishing to host port 1883. The stack reuses the **existing** Docker volumes named in `compose.yaml`; do not run the old standalone MongoDB, EMQX, or Redis containers against those volumes at the same time. The pinned MongoDB, EMQX, Redis, and Keycloak image digests match the images inspected on this host during the 2026-11-08 cutover. MongoDB's preserved replica set identifies its member as `ce8dad09ce13:27017`, so the Compose service keeps that hostname; changing it requires a deliberate replica-set reconfiguration.

Before the first cutover, record `docker ps`, MongoDB collection counts, and `emqx ctl status`. Export EMQX configuration and built-in data with `docker exec emqx emqx ctl data export`; keep the resulting archive in its mounted `/opt/emqx/data/backup` directory. EMQX stores data under a node-name directory, so the new Compose service has the stable node name `emqx@node1.emqx.local`. Stop, but **do not remove**, the old `smartfarm-atlas`, `emqx`, and `redis` containers. Then start the stack from this directory:

```bash
docker stop smartfarm-atlas emqx redis
docker compose up -d --no-build
docker compose ps
docker compose exec emqx emqx ctl status
```

Import the exported EMQX archive into the new node with `docker compose exec emqx emqx ctl data import /opt/emqx/data/backup/<export-file>.tar.gz`, then check its Dashboard configuration and MQTT connections. Compose sets the retainer storage mode to `disc`: the old broker used `ram`, which lost retained messages on restart. The original stopped containers remain available for rollback. To roll back, run `docker compose down` and `docker start smartfarm-atlas emqx redis`; do not use `down -v` or delete the external volumes. Keycloak is already managed by this Compose project and retains `mugvn-smartfarm_keycloak-data`.

Browser URLs: Svelte frontend `http://localhost:5173/`, Keycloak `http://localhost:8085/`, older API demo `http://localhost:5198/demo/`, and API OpenAPI `http://localhost:5198/openapi/v1.json`. The API uses Keycloak's public localhost issuer and its private Compose metadata address. After the first start, run `./scripts/initialize-keycloak.sh` to add the API demo redirect to the existing browser client; the script preserves an existing farm administrator password.

The Datadog Agent is under the `observability` profile so the application stack can run until you supply an API key. Set `DD_SITE` and `DD_API_KEY` in the ignored `.env`, then run `docker compose --profile observability up -d datadog-agent`. It collects labeled stdout/stderr logs from every Compose service, Docker container metrics, and API/worker traces. The worker creates one root trace per MQTT message. Redis and MongoDB checks and Keycloak management metrics are configured under `datadog/conf.d`. EMQX's metrics endpoint requires a dedicated API key on EMQX 6.3: create one in the Dashboard, set `EMQX_METRICS_API_KEY` and `EMQX_METRICS_API_SECRET` in `.env`, and restart the Agent. Verify with `docker compose exec datadog-agent agent status` and check the `service:smartfarm-*` logs and `smartfarm-api`/`smartfarm-ingestion` traces in Datadog. The four Pi guest Agents are separate from this Compose Agent. Full service tracing, MongoDB Database Monitoring, sampling, verification, and rollback are documented in [`datadog/README.md`](datadog/README.md). The ignored `.env` also requires a unique `MONGO_DATADOG_PASSWORD`; use the existing value configured locally.

The new Svelte demo is in [`../../SmartFarm-FE`](../../SmartFarm-FE) and runs at **http://localhost:5173/**. Its README has the frontend commands. The older `/demo/` page remains available from the API.

## Start locally

Run these commands from this directory (`MUGVN-SmartFarm/MUGVN-SmartFarm`). Your existing MongoDB, EMQX, and Redis containers must already be running.

```bash
# If .env does not exist, copy .env.example and replace both example passwords.
# Implementation setup may already have created a permission-restricted .env.
docker compose up -d keycloak
# Wait until http://localhost:8085/realms/smartfarm/.well-known/openid-configuration responds.
./scripts/initialize-keycloak.sh
npm --prefix UI ci
npm --prefix UI run build
dotnet restore MUGVN-SmartFarm.sln
dotnet run --project MUGVN-SmartFarm.csproj --launch-profile https
```

In a second terminal, from this same directory:

```bash
dotnet run --project Ingestion/SmartFarm.Ingestion.csproj
```

Open **https://localhost:7017/demo/** and sign in with `FARM_ADMIN_USERNAME` and `FARM_ADMIN_PASSWORD` from `.env`. This is a Keycloak realm account, separate from `KEYCLOAK_ADMIN_USERNAME`, which manages Keycloak itself. No passwords or tokens belong in Git. Administrator initialization is idempotent and preserves an existing user's password; editing `.env` does not reset an existing password. The development certificate must be trusted by your browser; use `dotnet dev-certs https --trust` if needed.

OpenAPI is at **https://localhost:7017/openapi/v1.json**, and Swagger UI at **https://localhost:7017/swagger/**. The `.http` file contains requests for every endpoint. Endpoints require a bearer access token with audience `smartfarm-api` and realm role `farm-admin`. The API does not offer a password/token endpoint; use Keycloak's Authorization Code + PKCE flow. The browser's access and refresh tokens stay in memory.

The demo's times in the history controls are UTC. Device cards show local browser-formatted dates and distinguish measurement time from delivery time. EC/light retain the dataset's numerical scales. The chart excludes unavailable observations, displays hourly averages, and limits itself to 10,000 history records; API pages are capped at 1,000.

## Configuration and storage

Both processes bind `Mongo`, `Farm`, and `Redis` options. The worker also binds `Mqtt`; the API binds `Keycloak`. Shared device, telemetry, and history types live in `SmartFarm.Contracts`; `SmartFarm.Infrastructure` implements MongoDB storage and Redis notifications. The worker references these two projects, not the API project. Override settings with .NET environment variables (`Mongo__ConnectionString`, `Redis__ConnectionString`, `Mqtt__Host`, etc.) or per-project user secrets (when running in Development). Worker configuration is loaded from its output directory, independently of the shell's current directory. API settings are in root `appsettings.json` and `appsettings.Development.json`; worker settings are in `Ingestion/appsettings.json` and `Ingestion/appsettings.Development.json`.

Development defaults: MongoDB `127.0.0.1:27018`, database `MUGVN-Hackathon`, collection `SmartFarm`; MQTT `127.0.0.1:1883`; Redis `127.0.0.1:6379`; farm `farm-001`. MQTT credentials are optional settings and must match your broker's configuration. Keycloak is development-only at `http://localhost:8085`, with persistent local storage and no public registration. Production startup requires `Mongo__ConnectionString` and `Redis__ConnectionString` for both processes, `Mqtt__Host` for ingestion, and `Keycloak__Authority` for the API. Supply production values through environment configuration or a secret provider. Production requires HTTPS identity metadata, persistent protected data, and service-specific credentials. The default Compose services run the API and ingestion worker in Docker; the optional `observability` profile starts the Datadog Agent. The container deployment uses internal service addresses and preserves the public localhost Keycloak issuer.

The worker subscribes to `mugvn/farm/+/env` and `/avail`, preserving the current Python producer contract. API startup seeds only missing devices: pi4-1/node6, pi4-2/node18, pi4-3/node24, pi4-4/node38. Start the API before publishing for the first time; unknown or disabled devices are rejected and acknowledged. PATCH edits label, interval, or enabled state; source identity is immutable. DELETE decommissions and never removes history. A decommissioned identity remains reserved.

`SmartFarm` must have `timeStamp` as its Time Series time field and `sensor` as metadata. Startup rejects incompatible collections without replacing them. Stored layout:

```text
sensor: { farm_id, device_id, node_id }
timeStamp: BSON Date from producer timestamp
message_id: canonical UUID
emitted_at, ingested_at: BSON UTC dates
measurements: { AirTemperature, AirHumidity, SoilTemperature, SoilHumidity,
                Ph, Ec, Light, MissingFlag, AnomalyLabel, ViolatedVariableCount }
```

HTTP/Redis/SignalR contracts use snake_case, including `source_timestamp` and nested `measurements`. `devices` contains registration; `device_states` contains worker observations. Retained MQTT availability indicates the most recently observed broker connection state; it is not proof that sensor reporting is healthy. Freshness uses the original stored ingestion time, with a default stale threshold of twice the configured reporting interval. The API computes staleness when queried and the browser recomputes it locally between updates.

Latest means descending source timestamp, then emission timestamp, then message ID. Late history and explicit replay resets never regress current cards. History requires `from < to`, supports ranges up to 366 days, and returns `items` plus `next_cursor`. Keep device and time range unchanged when reusing a cursor. Aggregates use UTC hour/day buckets, include anomalous available readings, exclude missing observations from means, and return valid/missing counts. Empty buckets are omitted, not filled with zeros.

`GET /api/devices/{deviceId}/telemetry/received-history?from=...&to=...&limit=...&cursor=...` uses the same page shape and range limits, but filters `[from,to)` by MongoDB `ingested_at` and orders by ingestion time then message ID. Its cursor belongs to that device and time range. The Svelte charts use this endpoint for the actual last 24 hours, while showing the historical measurement time in tooltips.

## Delivery and recovery

Run **one ingestion instance**. It processes messages serially and checks stored IDs before inserting. Time Series has no unique constraint on message IDs; this is application-level deduplication, not exactly-once storage. On an ambiguous write response the same message is checked again before another insert; successfully stored readings retain their original ingestion time when device state is repaired.

The MQTT 5 subscriber has stable client ID `smartfarm-ingestion`, clean start disabled, and default session expiry 86,400 seconds. Database work precedes its acknowledgement. Broker session expiry, queued-message limits, persistence configuration, or broker storage failure can still cause loss. Device publisher PUBACK means acceptance by EMQX, not confirmed MongoDB storage. Existing Pi SQLite outboxes and replay cursors remain unchanged; no replay reset runs on startup.

Redis channels are `smartfarm:telemetry:persisted:v1` and `smartfarm:device-state:changed:v1`. Publication is bounded and fails fast while disconnected. A database-to-publication crash can miss a live update; there is no durable outbox. The API reconnects its subscriptions; its in-memory live forwarding buffer holds 256 events and may discard old updates during bursts. The frontend reconciles after SignalR reconnect, Redis recovery, and every 60 seconds. It buffers live events while fetching snapshots and merges by measurement ordering and stable ID.

`GET /health/live` reports process liveness. Authenticated `/api/status` reports storage readiness and Redis live availability. Both processes emit JSON logs with device/message identifiers and failure reasons; the worker logs MQTT connection, database retries, rejections, duplicates, and successful persistence. ASP.NET hosting request logs are suppressed to avoid logging SignalR query tokens. Datadog can collect these logs; custom dashboards and metrics export are deferred.

## Verification

```bash
# Normal automated suite (external MongoDB checks skip unless explicitly enabled).
dotnet test MUGVN-SmartFarm.sln
npm --prefix UI test

# Include tests that create and delete uniquely named smartfarm_test_* databases only.
SMARTFARM_TEST_MONGO_URI='mongodb://127.0.0.1:27018/?directConnection=true' \
  dotnet test MUGVN-SmartFarm.sln

# Build first; stop your development API/worker so ports 5198/7017 are free.
# Requires initialized Keycloak, existing infrastructure, Node, and mongosh.
dotnet build MUGVN-SmartFarm.sln
npm --prefix UI run test:e2e
```

The end-to-end harness uses real Keycloak PKCE login, MQTT, Redis, SignalR and MongoDB, isolated database/topics/channels, and temporary TCP proxies to interrupt test connections. It does not stop your existing containers. It verifies staggered updates, duplicates, late measurements, Redis failure/recovery, MongoDB failure/recovery, persistent MQTT sessions after worker restart, and API restart snapshots. It drops only its generated test database and stops its own child processes. It bypasses localhost certificate validation only within its Node test process. Logs remain under the printed temporary directory; credentials/tokens are not printed.

For the real Pi demonstration, start the API and worker, then use your existing QEMU replay commands. Four full QEMU machines are unnecessary for the isolated verification harness. No remote command/acknowledgement protocol, multi-farm UI, durable event processing, or multi-worker coordination is included.
