import type { components, operations } from './schema'
import {
  RULE_VERSION,
  type AdminBanStatus,
  type AdminHistoricalGamesPage,
  type AdminLoginRequest,
  type AdminReplay,
  type AdminSession,
  type AdminUserDetail,
  type AdminUsersPage,
  type AdminUsersParams,
} from './types'

type ApiProblem = components['schemas']['ErrorResponse'] & Record<string, unknown>

type JsonResponse<TOperation extends keyof operations> =
  operations[TOperation]['responses'] extends { 200: { content: { 'application/json': infer TResponse } } }
    ? TResponse
    : operations[TOperation]['responses'] extends { 204: unknown }
      ? undefined
      : never

type JsonRequest<TOperation extends keyof operations> =
  operations[TOperation] extends { requestBody: { content: { 'application/json': infer TRequest } } }
    ? TRequest
    : never

type AntiforgeryToken = { token: string; headerName: string }

let antiforgeryTokenPromise: Promise<AntiforgeryToken> | undefined
let adminAntiforgeryTokenPromise: Promise<AntiforgeryToken> | undefined

export class ApiError extends Error {
  readonly status: number
  readonly code: string
  readonly problem: ApiProblem

  constructor(status: number, problem: ApiProblem) {
    super(problem.detail ?? problem.title ?? `请求失败（${status}）`)
    this.name = 'ApiError'
    this.status = status
    this.code = problem.code ?? 'REQUEST_FAILED'
    this.problem = problem
  }
}

async function getAntiforgeryToken(admin: boolean): Promise<AntiforgeryToken> {
  const current = admin ? adminAntiforgeryTokenPromise : antiforgeryTokenPromise
  const pending = current ?? request<AntiforgeryToken>(
    admin ? '/api/admin/antiforgery/token' : '/api/antiforgery/token',
  )
  if (admin) adminAntiforgeryTokenPromise = pending
  else antiforgeryTokenPromise = pending

  try {
    return await pending
  } catch (error) {
    if (admin) adminAntiforgeryTokenPromise = undefined
    else antiforgeryTokenPromise = undefined
    throw error
  }
}

async function request<TResponse>(
  path: string,
  init?: RequestInit,
): Promise<TResponse> {
  const headers = new Headers(init?.headers)
  const method = (init?.method ?? 'GET').toUpperCase()
  const adminRequest = path.startsWith('/api/admin/')
  headers.set('Accept', 'application/json')
  headers.set('X-Requested-With', 'MistChess')
  if (init?.body) headers.set('Content-Type', 'application/json')
  if (!['GET', 'HEAD', 'OPTIONS'].includes(method) && path !== '/api/sessions/guest') {
    const antiforgery = await getAntiforgeryToken(adminRequest)
    headers.set(antiforgery.headerName, antiforgery.token)
  }

  const response = await fetch(path, {
    ...init,
    credentials: init?.credentials ?? 'include',
    headers,
  })

  if (!response.ok) {
    let problem: ApiProblem = {
      code: 'REQUEST_FAILED',
      title: response.statusText || 'Request failed',
    }
    try {
      problem = (await response.json()) as ApiProblem
    } catch {
      // The HTTP status remains sufficient when a proxy returns a non-JSON error page.
    }

    if (typeof window !== 'undefined') {
      if (!adminRequest && problem.code === 'PLAYER_BANNED') {
        window.dispatchEvent(new CustomEvent<string>('mistchess:account-banned', {
          detail: typeof problem.detail === 'string' ? problem.detail : '',
        }))
      } else if (response.status === 401 && adminRequest) {
        adminAntiforgeryTokenPromise = undefined
        window.dispatchEvent(new Event('mistchess:admin-session-invalid'))
      } else if (response.status === 401 && path !== '/api/sessions/guest') {
        window.dispatchEvent(new Event('mistchess:session-invalid'))
      }
    }
    throw new ApiError(response.status, problem)
  }

  if (response.status === 204) return undefined as TResponse
  return (await response.json()) as TResponse
}

async function requestJson<TOperation extends keyof operations>(
  path: string,
  init?: RequestInit,
): Promise<JsonResponse<TOperation>> {
  return request<JsonResponse<TOperation>>(path, init)
}

function jsonBody<T>(value: T): string {
  return JSON.stringify(value)
}

export const api = {
  getAdminSession: () =>
    request<AdminSession>('/api/admin/session'),

  loginAdmin: async (credentials: AdminLoginRequest) => {
    adminAntiforgeryTokenPromise = undefined
    const session = await request<AdminSession>('/api/admin/session', {
      method: 'POST',
      body: jsonBody(credentials),
    })
    adminAntiforgeryTokenPromise = undefined
    return session
  },

  logoutAdmin: async () => {
    try {
      await request<void>('/api/admin/session', { method: 'DELETE' })
    } finally {
      adminAntiforgeryTokenPromise = undefined
    }
  },

  getAdminUsers: (params: AdminUsersParams) => {
    const search = new URLSearchParams()
    if (params.query) search.set('query', params.query)
    if (params.status && params.status !== 'all') search.set('status', params.status)
    if (params.online && params.online !== 'all') search.set('online', params.online)
    if (params.cursor) search.set('cursor', params.cursor)
    if (params.limit) search.set('limit', params.limit.toString())
    const query = search.size ? `?${search.toString()}` : ''
    return request<AdminUsersPage>(`/api/admin/users${query}`)
  },

  getAdminUser: (playerId: string) =>
    request<AdminUserDetail>(`/api/admin/users/${encodeURIComponent(playerId)}`),

  banAdminUser: (playerId: string, reason: string) =>
    request<AdminBanStatus>(`/api/admin/users/${encodeURIComponent(playerId)}/ban`, {
      method: 'POST',
      body: jsonBody({ reason }),
    }),

  unbanAdminUser: (playerId: string) =>
    request<AdminBanStatus>(`/api/admin/users/${encodeURIComponent(playerId)}/ban`, {
      method: 'DELETE',
    }),

  getAdminUserGames: (
    playerId: string,
    params: { cursor?: string; limit?: number },
  ) => {
    const search = new URLSearchParams()
    if (params.cursor) search.set('cursor', params.cursor)
    if (params.limit) search.set('limit', params.limit.toString())
    const query = search.size ? `?${search.toString()}` : ''
    return request<AdminHistoricalGamesPage>(
      `/api/admin/users/${encodeURIComponent(playerId)}/games${query}`,
    )
  },

  getAdminReplay: (gameId: string) =>
    request<AdminReplay>(`/api/admin/games/${encodeURIComponent(gameId)}/replay`),

  startGuestSession: () =>
    requestJson<'createGuestSession'>('/api/sessions/guest', { method: 'POST' }),

  heartbeatSession: () =>
    requestJson<'heartbeatGuestSession'>('/api/sessions/heartbeat', { method: 'POST' }),

  getGameOptions: () =>
    requestJson<'getGameOptions'>('/api/game-options'),

  getGameHistory: (params: {
    cursor?: string
    limit?: number
    ruleVersion?: string
    timeControl?: string
    result?: string
  }) => {
    const search = new URLSearchParams()
    if (params.cursor) search.set('cursor', params.cursor)
    if (params.limit) search.set('limit', params.limit.toString())
    if (params.ruleVersion) search.set('ruleVersion', params.ruleVersion)
    if (params.timeControl) search.set('timeControl', params.timeControl)
    if (params.result) search.set('result', params.result)
    const query = search.size ? `?${search.toString()}` : ''
    return requestJson<'getGameHistory'>(`/api/games/history${query}`)
  },

  createRoom: (settings: {
    timeControl: string | null
    moveTimeLimitSeconds: number | null
  }) =>
    requestJson<'createRoom'>('/api/rooms', {
      method: 'POST',
      body: jsonBody<JsonRequest<'createRoom'>>({
        ruleVersion: RULE_VERSION,
        ...settings,
      }),
    }),

  joinRoom: (code: string) =>
    requestJson<'joinRoom'>(`/api/rooms/${encodeURIComponent(code)}/join`, {
      method: 'POST',
    }),

  setRoomReady: (code: string, ready: boolean) =>
    requestJson<'setRoomReady'>(`/api/rooms/${encodeURIComponent(code)}/ready`, {
      method: 'POST',
      body: jsonBody<JsonRequest<'setRoomReady'>>({ ready }),
    }),

  leaveRoom: (code: string) =>
    requestJson<'leaveRoom'>(`/api/rooms/${encodeURIComponent(code)}/members/me`, {
      method: 'DELETE',
    }),

  createMatchTicket: (clientRequestId: string) =>
    requestJson<'createMatchTicket'>('/api/matchmaking/tickets', {
      method: 'POST',
      body: jsonBody<JsonRequest<'createMatchTicket'>>({
        ruleVersion: RULE_VERSION,
        clientRequestId,
      }),
    }),

  getCurrentMatchTicket: () =>
    requestJson<'getCurrentMatchTicket'>('/api/matchmaking/tickets/current'),

  heartbeatMatchTicket: (ticketId: string) =>
    requestJson<'heartbeatMatchTicket'>(
      `/api/matchmaking/tickets/${encodeURIComponent(ticketId)}/heartbeat`,
      { method: 'POST' },
    ),

  cancelMatchTicket: (ticketId: string) =>
    requestJson<'cancelMatchTicket'>(
      `/api/matchmaking/tickets/${encodeURIComponent(ticketId)}`,
      { method: 'DELETE' },
    ),

  getGame: (gameId: string) =>
    requestJson<'getGame'>(`/api/games/${encodeURIComponent(gameId)}`),

  submitMove: (gameId: string, move: JsonRequest<'submitMove'>) =>
    requestJson<'submitMove'>(`/api/games/${encodeURIComponent(gameId)}/moves`, {
      method: 'POST',
      body: jsonBody<JsonRequest<'submitMove'>>(move),
    }),

  resignGame: (gameId: string) =>
    requestJson<'resignGame'>(`/api/games/${encodeURIComponent(gameId)}/resign`, {
      method: 'POST',
    }),

  offerDraw: (gameId: string) =>
    requestJson<'offerDraw'>(`/api/games/${encodeURIComponent(gameId)}/draw-offers`, {
      method: 'POST',
    }),

  acceptDraw: (gameId: string) =>
    requestJson<'acceptDraw'>(
      `/api/games/${encodeURIComponent(gameId)}/draw-offers/accept`,
      { method: 'POST' },
    ),

  rejectDraw: (gameId: string) =>
    requestJson<'rejectDraw'>(
      `/api/games/${encodeURIComponent(gameId)}/draw-offers/reject`,
      { method: 'POST' },
    ),

  getReplay: (gameId: string) =>
    requestJson<'getReplay'>(`/api/games/${encodeURIComponent(gameId)}/replay`),

  createReplayShare: (gameId: string) =>
    requestJson<'createReplayShare'>(
      `/api/games/${encodeURIComponent(gameId)}/replay-share`,
      { method: 'POST' },
    ),

  revokeReplayShare: (gameId: string) =>
    requestJson<'revokeReplayShare'>(
      `/api/games/${encodeURIComponent(gameId)}/replay-share`,
      { method: 'DELETE' },
    ),

  getSharedReplay: (shareToken: string) =>
    requestJson<'getSharedReplay'>(
      `/api/replay-shares/${encodeURIComponent(shareToken)}`,
      { credentials: 'omit' },
    ),
}

export function errorMessage(error: unknown): string {
  if (error instanceof ApiError) return error.message
  if (error instanceof Error) return error.message
  return '网络请求失败，请稍后重试。'
}

export function matchCreatedGameId(error: unknown): string | null {
  if (!(error instanceof ApiError) || error.code !== 'MATCH_ALREADY_CREATED') {
    return null
  }
  return typeof error.problem.gameId === 'string' ? error.problem.gameId : null
}
