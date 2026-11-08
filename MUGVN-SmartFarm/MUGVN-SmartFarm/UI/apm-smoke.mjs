// Exercises deployed Compose services without inserting synthetic farm readings.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { randomBytes, createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import mqtt from 'mqtt';
import { HubConnectionBuilder, HttpTransportType, LogLevel } from '@microsoft/signalr';

const root = fileURLToPath(new URL('../', import.meta.url));
const env = Object.fromEntries(readFileSync(new URL('../.env', import.meta.url), 'utf8')
  .split('\n').filter(line => line && !line.startsWith('#')).map(line => {
    const index = line.indexOf('=');
    return [line.slice(0, index), line.slice(index + 1)];
  }));
const compose = (...args) => execFileSync('docker', ['compose', ...args], { cwd: root, stdio: 'pipe' });
const mongo = code => JSON.parse(execFileSync('mongosh', [
  'mongodb://127.0.0.1:27018/MUGVN-Hackathon?directConnection=true', '--quiet', '--eval', code
], { encoding: 'utf8' }));
const base = 'http://localhost:5173';
const auth = 'http://localhost:8085/realms/smartfarm/protocol/openid-connect';
const verifier = randomBytes(32).toString('base64url');
const params = new URLSearchParams({ client_id: 'smartfarm-demo', redirect_uri: base + '/',
  response_type: 'code', scope: 'openid', code_challenge_method: 'S256',
  code_challenge: createHash('sha256').update(verifier).digest('base64url') });
const login = await fetch(auth + '/auth?' + params, { redirect: 'manual' });
const action = (await login.text()).match(/<form[^>]*action="([^"]+)"/)?.[1]?.replaceAll('&amp;', '&');
assert.ok(action, 'Keycloak login form');
const signed = await fetch(action, { method: 'POST', redirect: 'manual', headers: {
  'content-type': 'application/x-www-form-urlencoded',
  cookie: login.headers.getSetCookie().map(value => value.split(';')[0]).join('; ')
}, body: new URLSearchParams({ username: env.FARM_ADMIN_USERNAME, password: env.FARM_ADMIN_PASSWORD }) });
assert.equal(signed.status, 302, 'Keycloak accepts the existing administrator');
const code = new URL(signed.headers.get('location')).searchParams.get('code');
const exchanged = await fetch(auth + '/token', { method: 'POST',
  headers: { 'content-type': 'application/x-www-form-urlencoded' },
  body: new URLSearchParams({ grant_type: 'authorization_code', client_id: 'smartfarm-demo',
    redirect_uri: base + '/', code, code_verifier: verifier }) });
assert.equal(exchanged.status, 200, 'PKCE token exchange');
const token = (await exchanged.json()).access_token;
async function checkApi() {
  for (const route of ['/api/status', '/api/devices']) {
    const response = await fetch(base + route, { headers: { Authorization: 'Bearer ' + token } });
    assert.equal(response.status, 200, 'Nginx proxies authenticated ' + route);
  }
}
await checkApi();
console.log('PASS Keycloak login and authenticated Nginx → API → MongoDB');

const original = mongo('print(JSON.stringify(db.SmartFarm.findOne({"sensor.device_id":"pi4-1"})))');
assert.ok(original, 'A pre-existing pi4-1 reading is required for the duplicate-only check');
const measurements = Object.fromEntries(Object.entries(original.measurements)
  .map(([key, value]) => [key.replace(/[A-Z]/g, (letter, index) => (index ? '_' : '') + letter.toLowerCase()), value]));
const payload = { ...measurements, ...original.sensor, message_id: original.message_id,
  timestamp: original.timeStamp, emitted_at: original.emitted_at };
const hub = new HubConnectionBuilder().withUrl(base + '/hubs/telemetry', {
  accessTokenFactory: () => token, transport: HttpTransportType.WebSockets, skipNegotiation: true
}).configureLogging(LogLevel.None).build();
let publisher;
let received;
hub.on('TelemetryPersisted', value => { if (value.message_id === payload.message_id) received = value; });
try {
  await hub.start();
  publisher = await mqtt.connectAsync('mqtt://127.0.0.1:1883', { reconnectPeriod: 0 });
  await publisher.publishAsync('mugvn/farm/pi4-1/env', JSON.stringify(payload), { qos: 1 });
  const deadline = Date.now() + 15000;
  while (!received && Date.now() < deadline) await new Promise(resolve => setTimeout(resolve, 100));
  assert.ok(received, 'duplicate telemetry reaches SignalR through Nginx');
  assert.equal(Date.parse(received.ingested_at), Date.parse(original.ingested_at), 'original receipt time is preserved');
  assert.equal('_trace' in received, false, 'SignalR event has no transport metadata');
  assert.equal(mongo('print(db.SmartFarm.countDocuments({message_id:' + JSON.stringify(payload.message_id) + '}))'), 1);
  console.log('PASS duplicate MQTT → MongoDB → Redis → SignalR through Nginx; stored reading preserved');
  if (process.env.SMARTFARM_TEST_AGENT_OUTAGE === '1') {
    try {
      compose('stop', 'datadog-agent');
      await checkApi();
      received = undefined;
      await publisher.publishAsync('mugvn/farm/pi4-1/env', JSON.stringify(payload), { qos: 1 });
      const end = Date.now() + 15000;
      while (!received && Date.now() < end) await new Promise(resolve => setTimeout(resolve, 100));
      assert.ok(received, 'telemetry continues during Agent outage');
      console.log('PASS API and telemetry delivery continue while Agent is stopped');
    } finally { compose('start', 'datadog-agent'); }
    await checkApi();
  }
} finally {
  if (publisher) await publisher.endAsync().catch(() => {});
  await hub.stop().catch(() => {});
}

// Close the WebSocket before inspecting its completed request log entry.
const nginxLogs = compose('logs', '--since', '2m', '--no-color', 'frontend').toString();
assert.equal(nginxLogs.includes(token), false, 'Nginx request logs must omit the bearer token');
console.log('PASS Nginx request logs omit the test bearer token');
