import { defineConfig, devices } from '@playwright/test';

// Runs against the full stack: the API serving the built Angular app, on a database
// seeded with the demo organizations (see the e2e job in .github/workflows/ci.yml).
const baseURL = process.env['E2E_BASE_URL'] ?? 'http://localhost:5080';
const chromium = process.env['CHROMIUM_PATH'];

export default defineConfig({
  testDir: './e2e',
  // The tests share one seeded database and change it, so they run in order.
  workers: 1,
  fullyParallel: false,
  retries: 0,
  forbidOnly: !!process.env['CI'],
  reporter: process.env['CI'] ? [['list'], ['html', { open: 'never' }]] : 'list',
  use: {
    baseURL,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    launchOptions: chromium ? { executablePath: chromium } : {},
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
});
