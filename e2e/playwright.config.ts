import { defineConfig, devices } from '../apps/web/node_modules/@playwright/test/index.js'
import { resolve } from 'node:path'

const webRoot = process.cwd()
const repositoryRoot = resolve(webRoot, '../..')
const externalBaseUrl = process.env.MISTCHESS_E2E_BASE_URL

export default defineConfig({
  testDir: '.',
  testMatch: '**/*.spec.ts',
  timeout: 90_000,
  expect: { timeout: 10_000 },
  fullyParallel: false,
  workers: 1,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? 'github' : 'list',
  use: {
    baseURL: externalBaseUrl ?? 'http://127.0.0.1:5173',
    ignoreHTTPSErrors: true,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
    {
      name: 'firefox',
      use: { ...devices['Desktop Firefox'] },
    },
    {
      name: 'mobile-chromium',
      use: { ...devices['Pixel 5'], browserName: 'chromium' },
    },
  ],
  webServer: externalBaseUrl
    ? undefined
    : [
        {
          command: 'dotnet run --project src/MistChess.Api --no-launch-profile --urls http://127.0.0.1:5052',
          cwd: repositoryRoot,
          env: {
            ...process.env,
            ASPNETCORE_ENVIRONMENT: 'Development',
            ReverseProxy__KnownProxies__0: '127.0.0.1',
            ReverseProxy__KnownProxies__1: '::1',
          },
          url: 'http://127.0.0.1:5052/health/live',
          reuseExistingServer: !process.env.CI,
          timeout: 120_000,
        },
        {
          command: 'npm run dev -- --host 127.0.0.1 --port 5173',
          cwd: webRoot,
          url: 'http://127.0.0.1:5173',
          reuseExistingServer: !process.env.CI,
          timeout: 60_000,
        },
      ],
})
