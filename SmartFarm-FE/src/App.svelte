<script lang="ts">
  import { onMount } from 'svelte';
  import Keycloak from 'keycloak-js';
  import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from '@microsoft/signalr';
  import NodeChart from './NodeChart.svelte';
  import { TelemetryStore, type Device, type HistoryPage, type Reading } from './telemetry';

  type BrowserConfig = { url: string; realm: string; clientId: string };
  const store = new TelemetryStore();
  let keycloak: Keycloak | undefined;
  let connection: HubConnection | undefined;
  let devices: Device[] = [];
  let chartData: Record<string, Reading[]> = {};
  let authenticated = false;
  let ready = false;
  let userName = '';
  let status = 'Connecting to SmartFarm…';
  let error = '';
  let liveAvailable = false;
  let refreshTask: Promise<void> | undefined;
  let stopped = false;
  let reconnectTimer: ReturnType<typeof setTimeout> | undefined;

  async function token(): Promise<string> {
    if (!keycloak) throw new Error('Sign-in is not ready');
    try {
      await keycloak.updateToken(40);
    } catch {
      await keycloak.login({ redirectUri: window.location.origin + '/' });
      throw new Error('Your session expired. Sign in again.');
    }
    if (!keycloak.token) throw new Error('No access token is available');
    return keycloak.token;
  }

  async function api<T>(path: string): Promise<T> {
    const response = await fetch(path, { headers: { Authorization: `Bearer ${await token()}` } });
    if (response.status === 403) throw new Error('Access denied. This account needs the farm-admin role.');
    if (!response.ok) throw new Error(`SmartFarm API request failed (${response.status}).`);
    return response.json() as Promise<T>;
  }

  function publishCharts(): void {
    store.trim();
    chartData = Object.fromEntries(devices.map(device => [device.device_id, store.forDevice(device.device_id)]));
  }

  async function recentHistory(deviceId: string, from: string, to: string): Promise<Reading[]> {
    const rows: Reading[] = [];
    const params = new URLSearchParams({ from, to, limit: '1000' });
    let cursor: string | null = null;
    do {
      if (cursor) params.set('cursor', cursor);
      const page = await api<HistoryPage>(
        `/api/devices/${encodeURIComponent(deviceId)}/telemetry/received-history?${params}`);
      rows.push(...page.items);
      cursor = page.next_cursor;
    } while (cursor);
    return rows;
  }

  async function refreshAll(): Promise<void> {
    if (refreshTask) return refreshTask;
    refreshTask = (async () => {
      const now = Date.now();
      const from = new Date(now - 24 * 60 * 60 * 1000).toISOString();
      const to = new Date(now + 1).toISOString();
      const [registered, health] = await Promise.all([
        api<Device[]>('/api/devices'), api<{ storage_ready: boolean; live_available: boolean }>('/api/status')
      ]);
      devices = registered.filter(device => !device.decommissioned);
      liveAvailable = health.live_available;
      await Promise.all(devices.map(async device => {
        store.merge(await recentHistory(device.device_id, from, to));
        publishCharts();
      }));
      publishCharts();
      status = health.storage_ready
        ? `MongoDB ready · live notifications ${liveAvailable ? 'available' : 'unavailable'}`
        : 'MongoDB is unavailable';
      error = '';
      ready = true;
    })().catch(e => {
      error = e instanceof Error ? e.message : String(e);
      status = 'Unable to refresh telemetry';
      ready = true;
    }).finally(() => { refreshTask = undefined; });
    return refreshTask;
  }

  function retryConnection(): void {
    if (stopped || reconnectTimer) return;
    reconnectTimer = setTimeout(() => {
      reconnectTimer = undefined;
      void connect();
    }, 5000);
  }

  async function connect(): Promise<void> {
    if (!connection || connection.state !== HubConnectionState.Disconnected) return;
    try {
      await connection.start();
      status = 'Live connection established';
      await refreshAll();
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
      status = 'Live connection unavailable; retrying';
      await refreshAll();
      retryConnection();
    }
  }

  async function start(): Promise<void> {
    try {
      const configResponse = await fetch('/demo/config.json');
      if (!configResponse.ok) throw new Error('Could not load Keycloak configuration from ASP.NET.');
      keycloak = new Keycloak(await configResponse.json() as BrowserConfig);
      authenticated = await keycloak.init({
        onLoad: 'login-required', pkceMethod: 'S256', checkLoginIframe: false,
        redirectUri: window.location.origin + '/'
      });
      if (!authenticated) return;
      userName = keycloak.tokenParsed?.preferred_username ?? 'Administrator';
      if (!keycloak.hasRealmRole('farm-admin')) {
        error = 'Access denied. This account needs the farm-admin role.';
        ready = true;
        return;
      }
      keycloak.onTokenExpired = () => { void token().catch(() => {}); };
      connection = new HubConnectionBuilder()
        .withUrl('/hubs/telemetry', { accessTokenFactory: token })
        .withAutomaticReconnect([0, 2000, 10000, 30000])
        .configureLogging(LogLevel.Warning)
        .build();
      connection.on('TelemetryPersisted', (reading: Reading) => {
        if (store.add(reading)) publishCharts();
      });
      connection.on('DeviceStateChanged', (event: {
        device_id: string; availability: string; last_received_at: string | null
      }) => {
        devices = devices.map(device => device.device_id === event.device_id
          ? { ...device, availability: event.availability,
              last_received_at: event.last_received_at ?? device.last_received_at,
              is_stale: event.last_received_at
                ? Date.now() - Date.parse(event.last_received_at) > device.reporting_interval_seconds * 2000
                : device.is_stale }
          : device);
      });
      connection.on('LiveStatus', (event: { available: boolean }) => {
        liveAvailable = event.available;
        status = `Live notifications ${liveAvailable ? 'available' : 'unavailable'}`;
        if (liveAvailable) void refreshAll();
      });
      connection.onreconnecting(() => { status = 'Live connection reconnecting…'; });
      connection.onreconnected(() => { void refreshAll(); });
      connection.onclose(() => { status = 'Live connection closed; retrying…'; retryConnection(); });
      await connect();
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
      status = 'Could not start SmartFarm';
      ready = true;
    }
  }

  async function signOut(): Promise<void> {
    if (keycloak) await keycloak.logout({ redirectUri: window.location.origin + '/' });
  }

  onMount(() => {
    void start();
    const timer = setInterval(() => {
      if (authenticated) void refreshAll();
      publishCharts();
    }, 60000);
    return () => {
      stopped = true;
      clearInterval(timer);
      if (reconnectTimer) clearTimeout(reconnectTimer);
      void connection?.stop();
    };
  });
</script>

<svelte:head>
  <meta name="description" content="Live MongoDB telemetry from SmartFarm Raspberry Pi nodes" />
</svelte:head>

<header class="site-header">
  <div class="header-inner">
    <div><p class="eyebrow">MUGVN · SMARTFARM</p><h1>Live farm telemetry</h1></div>
    {#if authenticated}<div class="account"><span>{userName}</span><button class="ghost" onclick={signOut}>Sign out</button></div>{/if}
  </div>
</header>

<main>
  <section class="intro">
    <div><p class="eyebrow">RASPBERRY PI NETWORK</p><h2>Sensor readings as they arrive</h2>
      <p>Each graph shows readings received by MongoDB during the past 24 hours. The original measurement time appears in each point’s tooltip.</p></div>
    {#if authenticated && !error}<button class="refresh" onclick={() => void refreshAll()}>Refresh data</button>{/if}
  </section>
  <p class:error class="system-status" role="status"><span class:active={liveAvailable} class="status-dot"></span>{status}</p>
  {#if error}<div class="alert" role="alert">{error}</div>{/if}
  {#if authenticated && ready}
    {#if devices.length === 0}<div class="empty">No active Raspberry Pi nodes are registered.</div>{/if}
    <section class="nodes" aria-label="Raspberry Pi telemetry charts">
      {#each devices as device (device.device_id)}
        <NodeChart {device} readings={chartData[device.device_id] ?? []} />
      {/each}
    </section>
  {:else if !ready}<div class="loading">Signing in and loading telemetry…</div>{/if}
</main>
