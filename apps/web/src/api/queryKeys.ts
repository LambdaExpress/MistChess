export const queryKeys = {
  session: ['session'] as const,
  currentTicket: ['matchmaking', 'current'] as const,
  room: (code: string) => ['room', code] as const,
  game: (gameId: string) => ['game', gameId] as const,
  replay: (gameId: string) => ['game', gameId, 'replay'] as const,
}
