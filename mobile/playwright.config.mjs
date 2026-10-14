import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: './tests/browser',
  fullyParallel: false,
  workers: 1,
  use: {
    ...devices['iPhone 13'],
    baseURL: 'http://127.0.0.1:5174',
    trace: 'retain-on-failure',
  },
  webServer: {
    command: 'node node_modules/vite/bin/vite.js --config tests/browser/vite.config.mjs',
    url: 'http://127.0.0.1:5174',
    reuseExistingServer: false,
  },
});
