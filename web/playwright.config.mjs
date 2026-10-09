import { defineConfig } from '@playwright/test';
const executablePath = process.env.CHROMIUM_PATH;
export default defineConfig({
  testDir: './tests/ui',
  fullyParallel: true,
  workers: 2,
  retries: 0,
  use: { baseURL: 'http://127.0.0.1:4173', headless: true, viewport: { width: 1440, height: 1100 }, launchOptions: { executablePath } },
  webServer: { command: 'npm run build && npm run preview -- --port 4173 --strictPort', url: 'http://127.0.0.1:4173', reuseExistingServer: !process.env.CI },
  reporter: 'list'
});
