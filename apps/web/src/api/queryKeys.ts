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
}
