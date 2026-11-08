<script lang="ts">
  import { onMount } from 'svelte';
  import * as echarts from 'echarts/core';
  import { LineChart, ScatterChart } from 'echarts/charts';
  import { GridComponent, TooltipComponent } from 'echarts/components';
  import { CanvasRenderer } from 'echarts/renderers';
  import type { Device, Measurements, Reading } from './telemetry';

  export let device: Device;
  export let readings: Reading[] = [];

  echarts.use([LineChart, ScatterChart, GridComponent, TooltipComponent, CanvasRenderer]);

  type Metric = keyof Pick<Measurements, 'air_temperature' | 'air_humidity' | 'soil_temperature' |
    'soil_humidity' | 'ph' | 'ec' | 'light'>;
  const metrics: { key: Metric; label: string; unit: string }[] = [
    { key: 'air_temperature', label: 'Air temperature', unit: '°C' },
    { key: 'air_humidity', label: 'Air humidity', unit: '%' },
    { key: 'soil_temperature', label: 'Soil temperature', unit: '°C' },
    { key: 'soil_humidity', label: 'Soil humidity', unit: '%' },
    { key: 'ph', label: 'pH', unit: '' },
    { key: 'ec', label: 'EC', unit: 'dataset scale' },
    { key: 'light', label: 'Light', unit: 'dataset scale' }
  ];
  let metric: Metric = 'air_temperature';
  let element: HTMLDivElement;
  let chart: echarts.ECharts | undefined;

  const formatTime = (value: string) => new Date(value).toLocaleString();

  function renderChart(rows: Reading[], selected: Metric): void {
    if (!chart) return;
    const setting = metrics.find(item => item.key === selected)!;
    const valid = rows.map(reading => ({
      value: [reading.ingested_at, reading.measurements.missing_flag ? null : reading.measurements[selected]],
      reading
    }));
    const missing = rows.filter(reading => reading.measurements.missing_flag).map(reading => ({
      value: [reading.ingested_at, 0.5], reading
    }));
    chart.setOption({
      animationDurationUpdate: 250,
      grid: { top: 20, left: 55, right: 24, bottom: 50 },
      tooltip: {
        trigger: 'item',
        formatter: (item: { data: { reading: Reading } }) => {
          const reading = item.data.reading;
          const value = reading.measurements.missing_flag
            ? 'Missing observation'
            : `${reading.measurements[selected].toFixed(2)} ${setting.unit}`;
          return `<strong>Received: ${formatTime(reading.ingested_at)}</strong><br/>` +
            `Measured: ${formatTime(reading.source_timestamp)}<br/>${setting.label}: ${value}`;
        }
      },
      xAxis: { type: 'time', name: 'Received time', nameLocation: 'middle', nameGap: 28 },
      yAxis: [
        { type: 'value', name: setting.unit, scale: true },
        { type: 'value', min: 0, max: 1, show: false }
      ],
      series: [
        { name: setting.label, type: 'line', data: valid, showSymbol: true, symbolSize: 7,
          connectNulls: false, lineStyle: { color: '#18a999', width: 2 }, itemStyle: { color: '#18a999' } },
        { name: 'Missing observation', type: 'scatter', yAxisIndex: 1, data: missing,
          symbol: 'diamond', symbolSize: 11, itemStyle: { color: '#e88944' } }
      ]
    }, true);
  }

  onMount(() => {
    chart = echarts.init(element);
    renderChart(readings, metric);
    const observer = new ResizeObserver(() => chart?.resize());
    observer.observe(element);
    return () => { observer.disconnect(); chart?.dispose(); chart = undefined; };
  });

  $: if (chart && readings && metric) renderChart(readings, metric);
</script>

<article class="node-card">
  <div class="node-header">
    <div><p class="eyebrow">{device.device_id} · NODE {device.node_id}</p><h2>{device.label}</h2></div>
    <span class:offline={device.availability !== 'online'} class="node-status">{device.availability}</span>
  </div>
  <div class="node-meta">
    <span>{device.enabled ? 'Enabled' : 'Disabled'}</span>
    <span>Last received: {device.last_received_at ? formatTime(device.last_received_at) : 'Never'}</span>
    <span>{device.is_stale ? 'Telemetry stale' : 'Telemetry fresh'}</span>
  </div>
  <div class="chart-heading">
    <label for={'metric-' + device.device_id}>Sensor metric</label>
    <select id={'metric-' + device.device_id} bind:value={metric}>
      {#each metrics as item}<option value={item.key}>{item.label}</option>{/each}
    </select>
  </div>
  <div class="chart" bind:this={element} role="img" aria-label={`${device.label} ${metrics.find(x => x.key === metric)?.label} by received time`}></div>
  <p class="chart-note">{readings.length} readings in the last 24 hours · {readings.filter(r => r.measurements.missing_flag).length} missing</p>
  {#if readings.length === 0}<p class="empty-chart">No readings received in the last 24 hours.</p>{/if}
  <p class="legend"><span class="legend-valid"></span> Measurement <span class="legend-missing"></span> Missing observation</p>
</article>
