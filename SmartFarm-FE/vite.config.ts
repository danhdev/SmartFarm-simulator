import { defineConfig } from 'vite';
import { svelte } from '@sveltejs/vite-plugin-svelte';

const apiTarget = 'https://localhost:7017';

export default defineConfig({
  plugins: [svelte()],
  server: {
    host: '127.0.0.1',
    port: 5173,
    strictPort: true,
    proxy: {
      '/api': { target: apiTarget, changeOrigin: true, secure: false },
      '/hubs/telemetry': { target: apiTarget, changeOrigin: true, secure: false, ws: true },
      '/demo/config.json': { target: apiTarget, changeOrigin: true, secure: false }
    }
  }
});
