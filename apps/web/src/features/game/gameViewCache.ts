import type { DrawOffer, GameView, TakebackRequestView } from '../../api/types'

const negotiationFields = (
  source: GameView,
): Pick<
  GameView,
  'negotiationVersion' | 'drawOffer' | 'takebackRequest' | 'canRequestTakeback'
> => ({
  negotiationVersion: source.negotiationVersion,
  drawOffer: source.drawOffer,
  takebackRequest: source.takebackRequest,
  canRequestTakeback: source.canRequestTakeback,
})

function newestClock(current: GameView, incoming: GameView): GameView['clock'] {
  if (!current.clock) return incoming.clock
  if (!incoming.clock) return current.clock
  const currentTime = Date.parse(current.clock.serverTime)
  const incomingTime = Date.parse(incoming.clock.serverTime)
  if (Number.isNaN(currentTime)) return incoming.clock
  if (Number.isNaN(incomingTime)) return current.clock
  return incomingTime >= currentTime ? incoming.clock : current.clock
}

export function mergeGameView(
  current: GameView | undefined,
  incoming: GameView,
): GameView {
  if (!current) return incoming

  let merged = incoming.version > current.version
    ? incoming
    : current

  if (incoming.version === current.version) {
    merged = { ...current, clock: newestClock(current, incoming) }
  }

  if (incoming.negotiationVersion > current.negotiationVersion) {
    merged = { ...merged, ...negotiationFields(incoming) }
  } else if (incoming.negotiationVersion < current.negotiationVersion) {
    merged = { ...merged, ...negotiationFields(current) }
  }

  return merged
}

export function mergeDrawOfferChange(
  current: GameView | undefined,
  offer: DrawOffer,
): GameView | undefined {
  if (!current || offer.revision <= current.negotiationVersion) return current
  return {
    ...current,
    negotiationVersion: offer.revision,
    drawOffer: offer.status === 'pending' ? offer : null,
    takebackRequest: null,
    canRequestTakeback: false,
  }
}

export function mergeTakebackRequestChange(
  current: GameView | undefined,
  request: TakebackRequestView,
): GameView | undefined {
  if (!current || request.revision <= current.negotiationVersion) return current
  return {
    ...current,
    negotiationVersion: request.revision,
    drawOffer: null,
    takebackRequest: request.status === 'pending' ? request : null,
    canRequestTakeback: false,
  }
}
