import { describe, expect, it } from 'vitest';
import { TelemetryStore, WINDOW_MS, type Reading } from './telemetry';

const now = Date.parse('2026-11-07T12:00:00Z');
function reading(id: string, device = 'pi4-1', received = now, missing = 0): Reading {
  return {
    message_id: id, device_id: device, node_id: device === 'pi4-1' ? 6 : 18, farm_id: 'farm-001',
    source_timestamp: '2025-07-24T00:00:00Z', emitted_at: new Date(received).toISOString(),
    ingested_at: new Date(received).toISOString(),
    measurements: { air_temperature: 25, air_humidity: 60, soil_temperature: 24,
      soil_humidity: 50, ph: 6.5, ec: 1.1, light: 1000, missing_flag: missing,
      anomaly_label: '', violated_variable_count: 0 }
  };
}

describe('TelemetryStore', () => {
  it('adds live readings only to their node and orders late delivery by received time', () => {
    const store = new TelemetryStore();
    store.add(reading('new', 'pi4-1', now), now);
    store.add(reading('other', 'pi4-2', now - 100), now);
    store.add(reading('old', 'pi4-1', now - 200), now);
    expect(store.forDevice('pi4-1').map(r => r.message_id)).toEqual(['old', 'new']);
    expect(store.forDevice('pi4-2').map(r => r.message_id)).toEqual(['other']);
  });

  it('deduplicates a live reading repeated in the MongoDB history refresh', () => {
    const store = new TelemetryStore();
    expect(store.add(reading('one'), now)).toBe(true);
    store.merge([reading('one'), reading('two')], now);
    expect(store.add(reading('one'), now)).toBe(false);
    expect(store.forDevice('pi4-1').map(r => r.message_id)).toEqual(['one', 'two']);
  });

  it('keeps missing observations identifiable instead of replacing their values', () => {
    const store = new TelemetryStore();
    store.add(reading('missing', 'pi4-1', now, 1), now);
    expect(store.forDevice('pi4-1')[0].measurements.missing_flag).toBe(1);
    expect(store.forDevice('pi4-1')[0].measurements.air_temperature).toBe(25);
  });

  it('keeps the inclusive 24-hour boundary and trims expired readings', () => {
    const store = new TelemetryStore();
    store.merge([reading('boundary', 'pi4-1', now - WINDOW_MS),
      reading('expired', 'pi4-1', now - WINDOW_MS - 1), reading('current')], now);
    expect(store.forDevice('pi4-1').map(r => r.message_id)).toEqual(['boundary', 'current']);
    store.trim(now + 1);
    expect(store.forDevice('pi4-1').map(r => r.message_id)).toEqual(['current']);
  });
});
