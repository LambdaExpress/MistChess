import type { GameView } from '../../api/types'

export function replaceWithAuthoritativeGameView(
  current: GameView | undefined,
  incoming: GameView,
): GameView {
  if (current && incoming.version < current.version) return current
  return incoming
}

export function replaceWithNewerGameView(
  current: GameView | undefined,
  incoming: GameView,
): GameView {
  if (current && incoming.version <= current.version) return current
  return incoming
}
