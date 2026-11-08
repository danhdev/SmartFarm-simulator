# SmartFarm tracing and MongoDB Database Monitoring

The Compose stack sends traces to the Datadog Agent on its private network.
The Pi IoT Agents and Python publishers are unchanged. Browser RUM is not enabled.

| Component | Service / visibility |
| --- | --- |
| Nginx | `smartfarm-frontend`: proxied API/configuration requests |
| API | `smartfarm-api`: HTTP requests, database calls, Redis consumers, SignalR sends |
| Worker | `smartfarm-ingestion`: one trace per MQTT processing attempt |
| EMQX | `smartfarm-emqx`: independent broker message-processing/delivery traces |
| Keycloak | `smartfarm-keycloak`: native OpenTelemetry authentication/request traces |
| MongoDB | Client spans plus DBM host `smartfarm-mongo`, cluster `smartfarm-local` |
| Redis | Client command spans plus existing Redis server metrics |

## Start and configuration

Run commands from the solution directory containing `compose.yaml`.
The ignored `.env` contains the existing `DD_SITE` and `DD_API_KEY`, plus a unique
`MONGO_DATADOG_PASSWORD`. The implementation generates the monitoring password
locally; copy only placeholders from `.env.example` when creating another environment.
Do not replace existing application or identity passwords.

```bash
docker compose --profile observability up -d --build
docker compose exec datadog-agent agent status
```

The pinned Agent is 7.85.2. Datadog .NET automatic/manual instrumentation is 3.57.0.
Nginx is AMD64 1.27.5 Alpine, using the checksum-pinned tracing-only Datadog module
1.27.0. The frontend image checks module loading with `nginx -t` during its build.
Ports 8126 (Datadog protocol) and 4317 (OTLP/gRPC) are not published to the host.
EMQX and Keycloak use `http://datadog-agent:4317` within Compose.

`SMARTFARM_VERSION` defaults to `local`; change it for each deployment.
`SMARTFARM_TRACE_SAMPLE_RATE` defaults to `1.0` and controls .NET, Nginx, and
Keycloak sampling. After changing it, recreate those services with Compose.
EMQX's legacy message tracer uses `trace_all=true` to include unchanged MQTT 3.1.1
publishers; it is independent of that sample-rate setting. Disable its tracing
with `EMQX_OPENTELEMETRY__TRACES__ENABLE=false` when broker spans are unnecessary.

Application services do not depend on the Agent being ready. An Agent outage can
lose trace data but does not prevent HTTP requests, persistence, or live delivery.
The `HOST_PROC=/proc` Agent setting avoids the documented Docker OTLP startup issue.

## Trace boundaries

Expected HTTP flow:

```text
smartfarm-frontend: Nginx request
  smartfarm-api: ASP.NET request
    MongoDB/Redis client spans
```

Expected telemetry flow:

```text
smartfarm-ingestion: mqtt.message.process (one attempt)
  telemetry.validate-source
  telemetry.persist
    mongodb.query
  redis.notification.publish
    redis.command
    smartfarm-api: redis.notification.consume
      signalr.send
```

Redis event JSON retains its original fields and adds optional `_trace` metadata.
Only supported propagation headers are included. Consumers accept old events and
malformed/missing metadata; no valid parent means a new trace. Context is activated
after dequeueing, separately for each event. SignalR sends use typed DTOs and never
include `_trace`. HTTP responses, MQTT payloads, and MongoDB documents are unchanged.

EMQX traces end at broker delivery. The worker intentionally starts a separate
trace and does not extract the broker's context. Failed worker attempts close
before backoff. Spans tag device/node/message identity, retry number, and outcome;
resource names do not include message IDs. Cancellation is tagged separately.

Static assets and Nginx WebSocket connections are not traced. API health and hub
requests are dropped by sampling rules; SignalR **server sends** are traced as
bounded operations, without claiming browser receipt or rendering time.
Keycloak health/metrics use its management interface. Authentication through a
browser redirect produces separate traces because browser RUM is not installed.

Nginx server access logs override the inherited default and use `$uri` rather
than query-bearing `$request`/`$request_uri`. An Agent processing rule masks query
credentials in all shipped logs, including Nginx upstream-error messages.
The API suppresses hosting diagnostics that can log SignalR query tokens, and the
Agent strips HTTP query strings from spans. No request bodies or authorization
headers are configured for capture. JSON console scopes support trace/log correlation.

## MongoDB Database Monitoring

The existing Atlas Local 8.3.13 single-node replica set is monitored directly.
Its image, hostname, authentication mode, data volumes, Time Series fields, and
profiling settings are preserved. The `datadog` account receives `clusterMonitor`
and read access to `admin`, `local`, and `MUGVN-Hackathon`.

The Mongo startup wrapper runs an idempotent bootstrap alongside the original
Atlas Local runner. On every restart it ensures that account and enables
`internalQueryStatsSampleRate=1.0`, including on existing data volumes. It retries
readiness for up to two minutes and reports failure without stopping MongoDB.
This local bootstrap relies on the deployment's existing `transitionToAuth` mode;
a deployment enforcing authentication needs an authenticated administration path.

The Agent enables DBM query metrics and collection/index statistics, restricted
to the farm database. Slow-operation samples come from MongoDB logs with profiling
level `0` and `slowms=100`; enabling full database profiling is unnecessary.
Client query spans and DBM are verified separately. Direct linking of an individual
MongoDB query execution to an APM trace is not configured.

## Verify

Use APM → Traces → Live and search for each service with `env:local`, for example:

```text
env:local service:smartfarm-api
env:local service:smartfarm-ingestion
env:local service:smartfarm-frontend
env:local service:smartfarm-emqx
env:local service:smartfarm-keycloak
```

Open Database Monitoring and locate `smartfarm-mongo` / `smartfarm-local`.
Agent status should show a running APM receiver, running OTLP collector, a healthy
Mongo check, and DBM query metrics/samples. MongoDB/Redis are dependencies in traces;
their server processes do not receive .NET tracers. The existing EMQX Prometheus check requires separate `EMQX_METRICS_API_KEY` and
`EMQX_METRICS_API_SECRET` credentials. A 401 on that check does not stop broker
OTLP tracing. Trace ingestion and indexed
retention are separate; these changes do not create Datadog retention filters.

```bash
# Existing data is required; this republishes one duplicate and preserves its receipt time.
node UI/apm-smoke.mjs

# Explicitly exercise Agent stop/start; the script restores the Agent in finally.
SMARTFARM_TEST_AGENT_OUTAGE=1 node UI/apm-smoke.mjs

# Existing failure/recovery suite: isolated DB/topics/channels and a free host HTTP port.
SMARTFARM_E2E_HTTP_PORT=5199 npm --prefix UI run test:e2e
```

The smoke test requires local Node dependencies, `mongosh`, the existing farm
administrator credentials, and the deployed Compose services. The failure/recovery
suite also requires the local Debug .NET builds. Container image tracing is automatic;
host-launched .NET processes need the profiler installed/enabled to produce spans.

## Rollback

Preserve all external volumes. Never use `down -v` or attach the old standalone
containers to the same volumes while Compose is running.

To disable new tracing, set `.NET` `DD_TRACE_ENABLED=false`, Nginx
`datadog_tracing off` in its traced locations, Keycloak `KC_TRACING_ENABLED=false`,
and EMQX `EMQX_OPENTELEMETRY__TRACES__ENABLE=false`, then recreate those services.
Restore previously recorded image IDs if an image rollback is required.
Redis metadata is backward compatible; application data needs no migration.

To restore pre-DBM Mongo behavior, first remove its observability entrypoint and
mount, leaving the original runner command and all data volumes. Disable `dbm`
and query metrics in the Mongo Agent check. Then restore the previous runtime value:

```bash
docker compose exec mongo mongosh --quiet --eval \
  'db.adminCommand({setParameter:1,internalQueryStatsSampleRate:0})'
```

`internalQueryStatsRateLimit` remains at its original `0`; profiling remains `0`
with `slowms=100`. The monitoring account can remain unused. To restore the prior
Agent image, use `registry.datadoghq.com/agent:7.76.1` with the prior Agent config.
