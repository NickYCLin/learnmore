import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: './tests/browser', testMatch: 'diagnostic-audio.spec.mjs', workers: 1,
  use: { ...devices['iPhone 13'] },
  projects: [
    { name: 'diagnostic-off', use: { baseURL: 'http://127.0.0.1:5174' } },
    { name: 'diagnostic-on', use: { baseURL: 'http://127.0.0.1:5185' } },
  ],
  webServer: [
    { command: 'node node_modules/vite/bin/vite.js --config tests/browser/vite.config.mjs', url: 'http://127.0.0.1:5174', reuseExistingServer: false, env: { VITE_NATIVE_AUDIO_DIAGNOSTIC: '0' } },
    { command: 'node node_modules/vite/bin/vite.js --host 127.0.0.1 --port 5185 --strictPort', url: 'http://127.0.0.1:5185', reuseExistingServer: false, env: { VITE_NATIVE_AUDIO_DIAGNOSTIC: '1' } },
  ],
});
