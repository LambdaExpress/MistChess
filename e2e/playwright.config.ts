import { existsSync, readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { defineConfig, devices } from '../apps/web/node_modules/@playwright/test/index.js'

const e2eRoot = __dirname
const repositoryRoot = resolve(e2eRoot, '..')
const webRoot = resolve(repositoryRoot, 'apps/web')
const externalBaseUrl = process.env.MISTCHESS_E2E_BASE_URL?.trim() || undefined
const reuseExistingServers = process.env.MISTCHESS_E2E_REUSE_SERVERS === '1'

function loadLocalEnvironment(path: string): Record<string, string> {
  if (!existsSync(path)) return {}

  const values: Record<string, string> = {}
  for (const sourceLine of readFileSync(path, 'utf8').split(/\r?\n/)) {
    const line = sourceLine.trim()
    if (!line || line.startsWith('#')) continue
    const separator = line.indexOf('=')
    if (separator <= 0) continue

    const key = line.slice(0, separator).trim()
    if (!key || process.env[key] !== undefined) continue
    let value = line.slice(separator + 1).trim()
    if (
      value.length >= 2
      && ((value.startsWith('"') && value.endsWith('"'))
        || (value.startsWith("'") && value.endsWith("'")))
    ) {
      value = value.slice(1, -1)
    }
    values[key] = value
  }
  return values
}

const apiEnvironment: Record<string, string> = {
  ...loadLocalEnvironment(resolve(repositoryRoot, '.env')),
  ASPNETCORE_ENVIRONMENT: 'Development',
  ReverseProxy__KnownProxies__0: '127.0.0.1',
  ReverseProxy__KnownProxies__1: '::1',
  MISTCHESS_E2E_ADMIN_PASSWORD: '',
}
if (process.env.MISTCHESS_E2E_ADMIN_USERNAME) {
  apiEnvironment.Admin__Username = process.env.MISTCHESS_E2E_ADMIN_USERNAME
}
if (process.env.MISTCHESS_E2E_ADMIN_PASSWORD_HASH) {
  apiEnvironment.Admin__PasswordHash = process.env.MISTCHESS_E2E_ADMIN_PASSWORD_HASH
}

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
    trace: 'off',
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
          command: [
            'dotnet tool restore &&',
            'dotnet tool run dotnet-ef database update',
            '--project src/MistChess.Infrastructure/MistChess.Infrastructure.csproj',
            '--startup-project src/MistChess.Api/MistChess.Api.csproj',
            '&& dotnet run --project src/MistChess.Api',
            '--no-launch-profile --urls http://127.0.0.1:5052',
          ].join(' '),
          cwd: repositoryRoot,
          env: apiEnvironment,
          url: 'http://127.0.0.1:5052/health/ready',
          reuseExistingServer: reuseExistingServers,
          timeout: 120_000,
        },
        {
          command: 'npm run dev -- --host 127.0.0.1 --port 5173',
          cwd: webRoot,
          env: {
            MISTCHESS_API_PROXY_TARGET: 'http://127.0.0.1:5052',
            MISTCHESS_E2E_ADMIN_PASSWORD: '',
          },
          url: 'http://127.0.0.1:5173',
          reuseExistingServer: reuseExistingServers,
          timeout: 60_000,
        },
      ],
})
