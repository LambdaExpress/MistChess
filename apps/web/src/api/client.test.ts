import { afterEach, describe, expect, it, vi } from 'vitest'

function jsonResponse(body: unknown): Response {
  return {
    ok: true,
    status: 200,
    statusText: 'OK',
    json: async () => body,
  } as Response
}

afterEach(() => {
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

describe('administrator antiforgery tokens', () => {
  it('fetches a fresh token before reauthentication', async () => {
    vi.resetModules()
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(jsonResponse({
        token: 'old-principal-token',
        headerName: 'X-CSRF-TOKEN',
      }))
      .mockResolvedValueOnce(jsonResponse({}))
      .mockResolvedValueOnce(jsonResponse({
        token: 'anonymous-login-token',
        headerName: 'X-CSRF-TOKEN',
      }))
      .mockResolvedValueOnce(jsonResponse({
        username: 'operator',
        expiresAt: '2099-07-27T08:00:00Z',
      }))
    vi.stubGlobal('fetch', fetchMock)
    const { api } = await import('./client')

    await api.banAdminUser('00000000-0000-0000-0000-000000000001', 'test ban')
    await api.loginAdmin({ username: 'operator', password: 'secret' })

    expect(fetchMock.mock.calls.map(([path]) => path)).toEqual([
      '/api/admin/antiforgery/token',
      '/api/admin/users/00000000-0000-0000-0000-000000000001/ban',
      '/api/admin/antiforgery/token',
      '/api/admin/session',
    ])
    const banHeaders = new Headers(fetchMock.mock.calls[1][1]?.headers)
    const loginHeaders = new Headers(fetchMock.mock.calls[3][1]?.headers)
    expect(banHeaders.get('X-CSRF-TOKEN')).toBe('old-principal-token')
    expect(loginHeaders.get('X-CSRF-TOKEN')).toBe('anonymous-login-token')
  })
})
