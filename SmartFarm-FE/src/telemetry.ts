export interface Measurements {
  air_temperature: number;
  air_humidity: number;
  soil_temperature: number;
  soil_humidity: number;
  ph: number;
  ec: number;
  light: number;
  missing_flag: number;
  anomaly_label: string;
  violated_variable_count: number;
}

export interface Reading {
  message_id: string;
  device_id: string;
  node_id: number;
  farm_id: string;
  source_timestamp: string;
  emitted_at: string;
  ingested_at: string;
  measurements: Measurements;
}

export interface Device {
  device_id: string;
  node_id: number;
  label: string;
  enabled: boolean;
  decommissioned: boolean;
  reporting_interval_seconds: number;
  availability: string;
  last_received_at: string | null;
  is_stale: boolean;
}

export interface HistoryPage {
  items: Reading[];
  next_cursor: string | null;
}

export const WINDOW_MS = 24 * 60 * 60 * 1000;

export class TelemetryStore {
  private readonly byDevice = new Map<string, Map<string, Reading>>();

  add(reading: Reading, now = Date.now()): boolean {
    const time = Date.parse(reading.ingested_at);
    if (!Number.isFinite(time) || time < now - WINDOW_MS || time > now) return false;
    let device = this.byDevice.get(reading.device_id);
    if (!device) {
      device = new Map();
      this.byDevice.set(reading.device_id, device);
    }
    if (device.has(reading.message_id)) return false;
    device.set(reading.message_id, reading);
    return true;
  }

  merge(readings: Reading[], now = Date.now()): void {
    for (const reading of readings) this.add(reading, now);
    this.trim(now);
  }

  trim(now = Date.now()): void {
    for (const [deviceId, readings] of this.byDevice) {
      for (const [id, reading] of readings) {
        const time = Date.parse(reading.ingested_at);
        if (!Number.isFinite(time) || time < now - WINDOW_MS || time > now) readings.delete(id);
      }
      if (readings.size === 0) this.byDevice.delete(deviceId);
    }
  }

  forDevice(deviceId: string): Reading[] {
    return [...(this.byDevice.get(deviceId)?.values() ?? [])].sort((a, b) =>
      Date.parse(a.ingested_at) - Date.parse(b.ingested_at) || a.message_id.localeCompare(b.message_id));
  }
}
