# Implementation verification

The solution contains the API, Shared, Ingestion, and Tests projects. The Pi Python source and real SQLite state were not modified. Verification uses independent MongoDB databases, Redis channels, and MQTT topics.

| Requirement | Automated evidence |
|---|---|
| Current producer contract, UTC conversion, valid anomalies | `ActualCsvFormat_PreservesSourceTimeAndAnomalousMeasurement`, `OffsetTimestamp_IsConvertedToUtc`, `UnavailableObservation_IsRetained` |
| Required fields, malformed values, identities, quality bounds | `MissingRequiredField_IsRejected`, `InvalidIdentityOrTimestamp_IsRejected`, `InvalidQualityOrNode_IsRejected`, `MalformedJson_IsRejected`, `NonFiniteMeasurement_IsRejected` |
| Registration and topic checks | `DisabledDecommissionedOrMismatchedDevice_CannotWrite`, `UnknownDevice_CannotWrite`, `Availability_RequiresMatchingDeviceAndKnownStatus` |
| Persist before publication | `NewReading_StoresBeforePublishing` |
| Duplicate suppression and original receipt time | `Duplicate_RepairsStateWithoutReinsertingOrChangingOriginalReceipt` |
| Ambiguous-write recovery and state repair | `AmbiguousInsert_RetryReadsStoredMeasurementAndRepairsState` |
| Redis failure independent of storage | `RedisFailure_DoesNotFailSuccessfulStorage`, real Redis proxy outage in `UI/e2e.mjs` |
| MongoDB BSON mapping, idempotent bootstrap, CRUD, indexes, source ordering, page boundaries, cursor ties, means and missing counts | `MongoStorage_MappingCrudOrderingPaginationAndAggregation` against a uniquely named database |
| Preserve an incompatible existing collection | `IncompatibleCollection_IsRejectedWithoutChangingItsData` |
| Cursor/range/page validation | `Cursor_RoundtripsAndCannotChangeRangeOrDevice`, `PageSizeOutsideBounds_IsRejected`, `EmptyOrReversedRange_IsRejected` |
| Device edits and soft decommission preserve source identity/history | `DeviceCrud_PreservesSourceIdentityAndHistoryOnDecommission`, `InvalidRegistration_IsRejected`, real authenticated CRUD in the end-to-end harness |
| API status codes, response names and error contract | `DeviceCreation_ReturnsLocationAndSnakeCaseResponse`, `MissingDevice_ReturnsProblemDetails` |
| JWT signature, issuer, audience, expiry and role checks | `TamperedSignature_IsRejected`, `InvalidJwt_IsRejected`, `ValidTokenWithoutFarmRole_IsForbidden`, `MalformedOrUnprivilegedRoleClaim_IsDenied`, `UnauthenticatedIdentity_CannotUseAdminClaim` |
| API and hub protection | `AnonymousApiAndHub_AreUnauthorized`, `Administrator_CanReadDevicesAndNegotiateHub`, `QueryStringToken_IsOnlyAcceptedOnHubRoute` |
| Real Keycloak password login using Authorization Code + PKCE | `UI/e2e.mjs` uses the initialized realm account, exchanges an authorization code, and calls the actual API/hub with the resulting token |
| OpenAPI routes, required query parameters and bearer security | `OpenApi_ListsApplicationEndpoints` |
| Freshness is independent of connectivity and uses a strict reporting threshold | `Freshness_UsesReceiptTimeWithStrictTwiceIntervalBoundary` and `freshness uses delivery time and has exact threshold` |
| Per-device display updates, late/reset readings and duplicate ordering | JS tests `staggered nodes update independently`, `late/reset reading never replaces newer source time`, `duplicate is ignored and equal times break ties deterministically` |
| Snapshot/live race and device-state ordering | JS tests `buffered live events survive initial and reconnect snapshots`, `device state merges availability and receipt independently` |
| Full MQTT → MongoDB → Redis → SignalR flow | Four staggered devices in `UI/e2e.mjs`; each event is awaited and its API state checked |
| MongoDB outage recovery | End-to-end harness interrupts only its own MongoDB TCP proxy, observes an ingestion retry, restores connectivity, and awaits the pending reading |
| Persistent MQTT session after worker restart | Harness stops its worker, publishes while it is offline, restarts the same client identity, and awaits queued delivery |
| API restart/reconnect | Harness restarts its API, verifies stored latest state, reconnects SignalR, and receives a subsequent new measurement |
| Demo assets | Locally bundled Keycloak/SignalR assets; harness checks HTML and bundle serving; frontend merge behavior has Node tests |

Run commands and prerequisites are in README.md. Expected WebSocket disconnect logs occur during the intentional API restart. The end-to-end harness temporarily bypasses the localhost development certificate in its own Node process; production authentication configuration remains unchanged.

## Verification limits

- Browser automation was unavailable in this environment. No visual/browser rendering review is claimed; the chart rendering and browser login UI were not exercised by a real browser.
- The API Release publish is checked separately from the optional Docker image. The documented and end-to-end-tested deployment runs .NET on the host with infrastructure in containers.
- This is one ingestion instance with application-level deduplication. No database-enforced exactly-once storage, durable Redis delivery, full broker power-loss guarantee, or multi-worker coordination is claimed.
- End-to-end sensor traffic comes from an isolated MQTT generator. Existing QEMU replay code and SQLite files remain unchanged.

## Results observed on 2026-11-05

- `SMARTFARM_TEST_MONGO_URI='mongodb://127.0.0.1:27018/?directConnection=true' dotnet test MUGVN-SmartFarm.sln`: **Passed 64, Failed 0, Skipped 0**.
- `npm --prefix UI test`: **Passed 6, Failed 0, Skipped 0**.
- `npm --prefix UI run build`: local browser bundle generated successfully.
- `npm --prefix UI run test:e2e`: all flow, outage, restart, and recovery checks passed. Latest run logs: `/tmp/smartfarm-e2e-YJs5YF`.
- API Release publish succeeded with zero reported warnings/errors; credentials and worker/test assemblies were absent from the API publish output.
- Keycloak administrator initialization was run again successfully without changing its password.
- Compose syntax and `git diff --check` passed.

Final default-service smoke check: the API and worker were started against the existing `MUGVN-Hackathon` database. Authenticated `/api/status` returned `storage_ready: true` and `live_available: true`; all four expected device/node mappings were returned; the demo returned HTTP 200. The existing telemetry collection remained empty, and no generated test databases remained. The API, worker, and newly added Keycloak service were left running for review.
# 2026-11-08: Docker APM and MongoDB DBM rollout

Implemented and deployed in the existing Compose project. This section records
current verification; earlier sections below describe earlier snapshots.

## Build and regression checks

- Final backend command: `SMARTFARM_TEST_MONGO_URI='mongodb://127.0.0.1:27018/?directConnection=true' dotnet test MUGVN-SmartFarm.sln -m:1 /p:UseSharedCompilation=false`.
  **Passed: 84, Failed: 0, Skipped: 0**, including all three isolated MongoDB integration tests.
- Legacy UI Node suite: passed. Svelte/Vitest suite: **4 passed**. Svelte check and Vite build: passed.
- API, ingestion, and frontend Docker builds: passed. Nginx 1.27.5 loaded the
  checksum-pinned Datadog module 1.27.0 and passed build-time `nginx -t`.
- Compose validation, shell/JavaScript syntax checks, and `git diff --check`: passed.

| Requirement | Exact test evidence |
| --- | --- |
| Trace headers and original event fields survive transport | `RedisTraceMessageTests.PropagatedHeaders_RoundTripWithoutChangingEventFields` |
| Events without tracing remain compatible | `RedisTraceMessageTests.LegacyEvent_IsReadableAndHasNoParentContext` |
| Invalid metadata does not reject valid events | `RedisTraceMessageTests.MalformedMetadata_DoesNotRejectTheEvent` (six cases) |
| Transport metadata stays out of browser DTOs; older readers remain compatible | `RedisTraceMessageTests.TypedSignalREvent_DoesNotExposeTransportMetadata` |
| Concurrent event carriers remain isolated | `RedisTraceMessageTests.ConcurrentEvents_KeepTheirOwnContextAndDevice` |
| Invalid JSON is rejected | `RedisTraceMessageTests.InvalidJson_IsRejectedRatherThanFabricatingAnEvent` |

## Live application and trace evidence

- The existing isolated `UI/e2e.mjs` suite passed with the actual Datadog .NET
  profiler enabled. It verified PKCE login, authenticated device operations,
  staggered telemetry, duplicates, late source ordering, Redis outage/recovery,
  MongoDB interruption/retry, worker restart/offline MQTT queueing, and API/SignalR
  restart recovery. A free HTTP port was selected with `SMARTFARM_E2E_HTTP_PORT=5199`.
- Native .NET spans were captured in a temporary localhost receiver. The capture
  contained automatic `mongodb.query` and `redis.command` spans, MQTT attempts,
  validation/persistence, Redis publish/consume, and SignalR sends. Matching actual
  trace IDs and parent span IDs verified **40 cross-service Redis consumer links**
  across the passing runs, with SignalR sends parented to the consumers.
- `UI/apm-smoke.mjs` passed against the deployed Compose stack using a pre-existing
  reading as a duplicate. It verified Keycloak login, Nginx → API requests, and
  MQTT → MongoDB → Redis → API → SignalR through Nginx. The stored reading count
  for that message remained one, its original receipt time was retained, and the
  SignalR DTO did not contain `_trace`.
- The explicit Agent-outage smoke test passed: authenticated API requests and
  duplicate telemetry delivery continued while the Agent was stopped; the test
  restored it in `finally`.
- Agent 7.85.2 reported a running APM receiver and OTLP collector. Native receiver
  counters accepted `smartfarm-frontend` and `smartfarm-api` payloads. Its service
  sampling state recognized `smartfarm-emqx` and `smartfarm-keycloak` with `env:local`.
  The APM writer reported no errors/retries in the final inspection window.
- Configured password/API-key values were absent from changed implementation/docs
  and captured .NET span payloads. Captured HTTP span tags did not contain
  `access_token=`. The final log scan caught and corrected Nginx's inherited duplicate access log.
  The server now overrides it, and the smoke test asserts its bearer token is absent
  from completed request logs. Agent-side masking also covers query credentials
  in upstream-error messages. API hosting diagnostics that can
  expose query tokens are suppressed, and the Agent removes HTTP query strings.

## Database Monitoring and restart evidence

- MongoDB remains Atlas Local **8.3.13** with the original image, hostname, replica
  set, and volumes. The Mongo check reported no errors/warnings and emitted DBM
  activity, metadata, query metrics, and query samples for `smartfarm-mongo` /
  `smartfarm-local`.
- A temporary `smartfarm_test_dbm_*` database, temporary read grant, and temporary
  standalone check configuration verified sampling outside the farm data. The
  check collected three activity events, one query-metrics event, and two query
  samples. One sample was a `source:slow_query` explain plan for the deliberately
  slow `probe` query (264 ms), with its JavaScript literal obfuscated.
- The isolated DBM database, temporary grant, and temporary check configuration
  were removed after verification.
- MongoDB was restarted. Bootstrap restored `internalQueryStatsSampleRate=1.0`;
  profiling remained **0**, `slowms` remained **100**, and the four device/state
  records remained present. The Agent was also restarted during the outage test.
- The farm had 152 readings at the initial snapshot and 166 at the post-restart
  snapshot. Additional readings arrived from the four continuing Pi replays;
  implementation smoke tests only republished an existing farm message. No farm
  collection was dropped or recreated, and no external volume was removed.

## Limits and existing issues

- Datadog's browser UI and indexed retention were not independently inspected.
  Local receiver/forwarder and DBM checks establish collection/export, not a claim
  about every trace being indexed or a specific UI screen rendering.
- The existing EMQX Prometheus metrics check returns **401 Unauthorized** until
  dedicated `EMQX_METRICS_API_KEY` / `EMQX_METRICS_API_SECRET` credentials are
  configured. This is separate from the working EMQX OTLP trace export. Keycloak's
  metrics check passed.
- EMQX and ingestion intentionally have separate traces. Browser receipt/render
  timing and individual MongoDB DBM-to-APM query linking are not configured.
- Setup, sampling, service searches, and rollback: [datadog/README.md](datadog/README.md).
