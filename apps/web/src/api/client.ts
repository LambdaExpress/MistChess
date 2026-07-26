import type { components, operations } from './schema'
import { RULE_VERSION } from './types'

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

let antiforgeryTokenPromise: Promise<JsonResponse<'getAntiforgeryToken'>> | undefined

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

async function getAntiforgeryToken(): Promise<JsonResponse<'getAntiforgeryToken'>> {
  antiforgeryTokenPromise ??= requestJson<'getAntiforgeryToken'>('/api/antiforgery/token')
  try {
    return await antiforgeryTokenPromise
  } catch (error) {
    antiforgeryTokenPromise = undefined
    throw error
  }
}

async function requestJson<TOperation extends keyof operations>(
  path: string,
  init?: RequestInit,
): Promise<JsonResponse<TOperation>> {
  const headers = new Headers(init?.headers)
  const method = (init?.method ?? 'GET').toUpperCase()
  headers.set('Accept', 'application/json')
  headers.set('X-Requested-With', 'MistChess')
  if (init?.body) headers.set('Content-Type', 'application/json')
  if (!['GET', 'HEAD', 'OPTIONS'].includes(method) && path !== '/api/sessions/guest') {
    const antiforgery = await getAntiforgeryToken()
    headers.set(antiforgery.headerName, antiforgery.token)
  }

  const response = await fetch(path, {
    ...init,
    credentials: 'include',
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
    throw new ApiError(response.status, problem)
  }

  if (response.status === 204) return undefined as JsonResponse<TOperation>
  return (await response.json()) as JsonResponse<TOperation>
}

function jsonBody<T>(value: T): string {
  return JSON.stringify(value)
}

export const api = {
  startGuestSession: () =>
    requestJson<'createGuestSession'>('/api/sessions/guest', { method: 'POST' }),

  createRoom: () =>
    requestJson<'createRoom'>('/api/rooms', {
      method: 'POST',
      body: jsonBody<JsonRequest<'createRoom'>>({
        ruleVersion: RULE_VERSION,
        timeControl: null,
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
        timeControl: null,
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
