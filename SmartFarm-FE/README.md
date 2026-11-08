# SmartFarm frontend

Svelte and TypeScript frontend for the SmartFarm local demo. It requires the existing Keycloak farm administrator account and uses the ASP.NET API for MongoDB history and SignalR live events. The browser never connects to MongoDB directly.

## Run locally

Start MongoDB, Redis, EMQX, and Keycloak as described in the backend [README](../MUGVN-SmartFarm/MUGVN-SmartFarm/README.md). From `MUGVN-SmartFarm/MUGVN-SmartFarm`, run `./scripts/initialize-keycloak.sh` once to allow the frontend's `http://localhost:5173/` redirect on an existing realm.

Start the API and ingestion worker in separate terminals from the backend directory:

```bash
dotnet run --project MUGVN-SmartFarm.csproj --launch-profile https
dotnet run --project Ingestion/SmartFarm.Ingestion.csproj
```

Then, from `SmartFarm-FE`:

```bash
npm ci
npm run dev
```

Open **http://localhost:5173/** and sign in with `FARM_ADMIN_USERNAME` and `FARM_ADMIN_PASSWORD` from the backend's `.env`. Vite proxies `/api`, `/hubs/telemetry`, and `/demo/config.json` to `https://localhost:7017`; it accepts the local ASP.NET development certificate only for that development proxy. Port 5173 is fixed so it matches the Keycloak redirect setting. If port 5198 is occupied, the API can bind only HTTPS with `ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=https://localhost:7017 dotnet run --no-launch-profile --project MUGVN-SmartFarm.csproj`.

Each node chart shows readings received by MongoDB in the rolling past 24 hours. This uses `ingested_at` because the replay's original `source_timestamp` is from July 2025. Hover a point to see both times. Missing observations appear as orange diamonds; they do not become numeric chart values. SignalR updates the charts immediately, and a periodic history refresh recovers missed notifications.

## Checks

```bash
npm run check
npm test
npm run build
npm run test:e2e
```

The browser test requires the local services above, installed Playwright Chromium, `mongosh`, and backend `.env` credentials. It signs in through Keycloak and publishes **one** test reading to pi4-1, which remains in the real MongoDB collection. Set `SMARTFARM_CHROMIUM_PATH` to a locally installed Chromium executable if the Playwright-managed binary is unavailable.
