import type { AdminUsersParams } from './types'

export const queryKeys = {
  session: ['session'] as const,
  gameOptions: ['game-options'] as const,
  currentTicket: ['matchmaking', 'current'] as const,
  room: (code: string) => ['room', code] as const,
  game: (gameId: string) => ['game', gameId] as const,
  history: (sessionId: string, ruleVersion: string, timeControl: string, result: string) =>
    ['history', sessionId, ruleVersion, timeControl, result] as const,
  privateReplay: (sessionId: string, gameId: string) =>
    ['private-replay', sessionId, gameId] as const,
  sharedReplay: (opaqueTokenKey: string) => ['shared-replay', opaqueTokenKey] as const,
  adminRoot: ['admin'] as const,
  adminSession: ['admin', 'session'] as const,
  adminUsersRoot: ['admin', 'users'] as const,
  adminUsers: (params: AdminUsersParams) => [
    'admin',
    'users',
    params.query ?? '',
    params.status ?? 'all',
    params.online ?? 'all',
    params.cursor ?? '',
    params.limit ?? 20,
  ] as const,
  adminUser: (playerId: string) => ['admin', 'users', playerId, 'detail'] as const,
  adminUserGames: (playerId: string) => ['admin', 'users', playerId, 'games'] as const,
  adminReplay: (gameId: string) => ['admin', 'games', gameId, 'replay'] as const,
}
