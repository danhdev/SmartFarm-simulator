// Local demo check. Requires the API, worker, Vite, Keycloak, MQTT, and MongoDB.
import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';
import { chromium } from 'playwright';
import mqtt from 'mqtt';

const project = resolve(fileURLToPath(new URL('../', import.meta.url)));
const envFile = readFileSync(resolve(project, '../MUGVN-SmartFarm/MUGVN-SmartFarm/.env'), 'utf8');
const env = Object.fromEntries(envFile.split('\n').filter(line => line && !line.startsWith('#')).map(line => {
  const i = line.indexOf('=');
  return [line.slice(0, i), line.slice(i + 1)];
}));
assert.ok(env.FARM_ADMIN_USERNAME && env.FARM_ADMIN_PASSWORD, 'Local farm administrator credentials are required');

const browser = await chromium.launch({
  headless: true,
  ...(process.env.SMARTFARM_CHROMIUM_PATH
    ? { executablePath: process.env.SMARTFARM_CHROMIUM_PATH } : {})
});
let publisher;
try {
  const page = await browser.newPage();
  page.on('pageerror', error => console.error('Browser error:', error.message));
  page.on('requestfailed', request => console.error('Request failed:', request.url(), request.failure()?.errorText));
  await page.goto('http://localhost:5173/', { waitUntil: 'domcontentloaded' });
  try {
    await page.locator('#username').waitFor({ timeout: 10000 });
  } catch {
    console.error('Login page URL:', page.url());
    console.error('Page text:', (await page.locator('body').innerText()).slice(0, 1200));
    throw new Error('Keycloak login form did not appear');
  }
  await page.locator('#username').fill(env.FARM_ADMIN_USERNAME);
  await page.locator('#password').fill(env.FARM_ADMIN_PASSWORD);
  await page.locator('#kc-login').click();
  await page.waitForURL('http://localhost:5173/');
  const cards = page.locator('.node-card');
  await cards.first().locator('.chart-note').waitFor();
  assert.equal(await cards.count(), 4, 'all four Raspberry Pi charts are present');
  const expectedNodes = ['pi4-1', 'pi4-2', 'pi4-3', 'pi4-4'];
  for (const node of expectedNodes) {
    const card = cards.filter({ hasText: node });
    assert.equal(await card.count(), 1, `${node} has one chart`);
    assert.ok((await card.locator('.chart-note').innerText()).match(/[1-9]\d* readings/),
      `${node} has received-time MongoDB history`);
    assert.equal(await card.locator('canvas').count(), 1, `${node} uses an ECharts canvas`);
  }
  if (process.env.SMARTFARM_SCREENSHOT)
    await page.screenshot({ path: process.env.SMARTFARM_SCREENSHOT, fullPage: true });
  console.log('PASS farm administrator signed in; all four charts loaded MongoDB history');

  const first = cards.filter({ hasText: 'pi4-1' });
  await first.locator('select').selectOption('soil_humidity');
  assert.match(await first.locator('.chart').getAttribute('aria-label'), /Soil humidity/);
  await first.locator('select').selectOption('air_temperature');
  const before = Number((await first.locator('.chart-note').innerText()).split(' ')[0]);
  const beforeImage = await first.locator('canvas').evaluate(canvas => canvas.toDataURL());
  const otherBefore = await Promise.all(expectedNodes.slice(1).map(async node =>
    cards.filter({ hasText: node }).locator('.chart-note').innerText()));
  const id = randomUUID();
  publisher = await mqtt.connectAsync('mqtt://127.0.0.1:1883',
    { clientId: 'smartfarm-fe-e2e-' + id, reconnectPeriod: 0 });
  await publisher.publishAsync('mugvn/farm/pi4-1/env', JSON.stringify({
    message_id: id, device_id: 'pi4-1', node_id: 6,
    timestamp: '2025-07-24T00:00:00Z', emitted_at: new Date().toISOString(),
    air_temperature: 26.5, air_humidity: 65, soil_temperature: 24,
    soil_humidity: 58, ph: 6.5, ec: 1.1, light: 22000,
    missing_flag: 0, anomaly_label: 'normal', violated_variable_count: 0
  }), { qos: 1 });
  await page.waitForFunction(expected => {
    const text = [...document.querySelectorAll('.node-card')]
      .find(card => card.textContent?.includes('pi4-1'))?.querySelector('.chart-note')?.textContent;
    return text && Number(text.split(' ')[0]) > expected;
  }, before, { timeout: 30000 });
  await page.waitForFunction(expected => {
    const canvas = [...document.querySelectorAll('.node-card')]
      .find(card => card.textContent?.includes('pi4-1'))?.querySelector('canvas');
    return canvas && canvas.toDataURL() !== expected;
  }, beforeImage, { timeout: 30000 });
  const otherAfter = await Promise.all(expectedNodes.slice(1).map(async node =>
    cards.filter({ hasText: node }).locator('.chart-note').innerText()));
  assert.deepEqual(otherAfter, otherBefore, 'the other node charts stay unchanged');
  const persisted = execFileSync('mongosh', [
    'mongodb://127.0.0.1:27018/MUGVN-Hackathon?directConnection=true', '--quiet', '--eval',
    `print(db.SmartFarm.countDocuments({message_id:'${id}'}))`
  ], { encoding: 'utf8' }).trim();
  assert.equal(persisted, '1', 'the new chart point is stored in MongoDB');
  console.log('PASS MQTT → MongoDB → SignalR updates pi4-1 without reloading or changing other charts');
  await page.getByRole('button', { name: 'Sign out' }).click();
  await page.locator('#username').waitFor({ timeout: 15000 });
  console.log('PASS administrator sign-out returns to Keycloak login');
} finally {
  if (publisher) await publisher.endAsync().catch(() => {});
  await browser.close();
}
