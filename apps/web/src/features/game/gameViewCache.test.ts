import { describe, expect, it } from 'vitest'
import type { DrawOffer, GameView, TakebackRequestView } from '../../api/types'
import {
  mergeDrawOfferChange,
  mergeGameView,
  mergeTakebackRequestChange,
} from './gameViewCache'

function snapshot(version: number, overrides: Partial<GameView> = {}): GameView {
  return {
    gameId: 'game-1',
    ruleVersion: 'fog-xiangqi-v1',
    timeControl: null,
    version,
    status: 'playing',
    result: null,
    perspective: 'red',
    sideToMove: 'red',
    visibleSquares: [],
    pieces: [],
    candidateMoves: [],
    captureSummary: { redLost: [], blackLost: [] },
    clock: null,
    drawOffer: null,
    negotiationVersion: 0,
    takebackRequest: null,
    lastAction: null,
    canRequestTakeback: false,
    ...overrides,
  }
}

function drawOffer(revision: number, status: DrawOffer['status'] = 'pending'): DrawOffer {
  return {
    id: `draw-${revision}`,
    offeredBy: 'black',
    status,
    revision,
  }
}

function takebackRequest(
  revision: number,
  status: TakebackRequestView['status'] = 'pending',
): TakebackRequestView {
  return {
    id: `takeback-${revision}`,
    status,
    requestedBy: 'black',
    requestedPly: 3,
    requestedAtVersion: 5,
    resolvedAtVersion: status === 'pending' ? null : 6,
    createdAt: '2026-07-31T00:00:00Z',
    revision,
  }
}

describe('mergeGameView', () => {
  it('uses the first snapshot as the cache baseline', () => {
    const incoming = snapshot(8)

    expect(mergeGameView(undefined, incoming)).toBe(incoming)
  })

  it('takes a newer game version without rolling back newer negotiation state', () => {
    const currentOffer = drawOffer(5)
    const current = snapshot(8, {
      negotiationVersion: 5,
      drawOffer: currentOffer,
    })
    const incoming = snapshot(9, {
      sideToMove: 'black',
      negotiationVersion: 4,
      takebackRequest: takebackRequest(4),
      canRequestTakeback: true,
    })

    expect(mergeGameView(current, incoming)).toMatchObject({
      version: 9,
      sideToMove: 'black',
      negotiationVersion: 5,
      drawOffer: currentOffer,
      takebackRequest: null,
      canRequestTakeback: false,
    })
  })

  it('takes newer negotiation state without rolling back the board version', () => {
    const request = takebackRequest(7)
    const current = snapshot(10, { sideToMove: 'black', negotiationVersion: 6 })
    const incoming = snapshot(9, {
      negotiationVersion: 7,
      takebackRequest: request,
    })

    expect(mergeGameView(current, incoming)).toMatchObject({
      version: 10,
      sideToMove: 'black',
      negotiationVersion: 7,
      drawOffer: null,
      takebackRequest: request,
    })
  })

  it('uses the newer server clock for equal game versions', () => {
    const current = snapshot(8, {
      clock: {
        redMilliseconds: 42_000,
        blackMilliseconds: 21_000,
        serverTime: '2026-07-31T00:00:01Z',
      },
    })
    const incoming = snapshot(8, {
      clock: {
        redMilliseconds: 41_000,
        blackMilliseconds: 21_000,
        serverTime: '2026-07-31T00:00:02Z',
      },
    })

    expect(mergeGameView(current, incoming).clock).toBe(incoming.clock)
    expect(mergeGameView(incoming, current).clock).toBe(incoming.clock)
  })
})

describe('negotiation notification merges', () => {
  it('ignores out-of-order draw and takeback notifications', () => {
    const base = snapshot(12, { canRequestTakeback: true })
    const offer = drawOffer(5)
    const withOffer = mergeDrawOfferChange(base, offer)

    expect(withOffer).toMatchObject({
      negotiationVersion: 5,
      drawOffer: offer,
      takebackRequest: null,
      canRequestTakeback: false,
    })

    const staleTakeback = takebackRequest(4)
    expect(mergeTakebackRequestChange(withOffer, staleTakeback)).toBe(withOffer)

    const request = takebackRequest(6)
    const withTakeback = mergeTakebackRequestChange(withOffer, request)
    expect(withTakeback).toMatchObject({
      negotiationVersion: 6,
      drawOffer: null,
      takebackRequest: request,
      canRequestTakeback: false,
    })
    expect(mergeDrawOfferChange(withTakeback, offer)).toBe(withTakeback)
  })

  it('applies a terminal notification only when it advances negotiation revision', () => {
    const pendingRequest = takebackRequest(7)
    const current = snapshot(12, {
      negotiationVersion: 7,
      takebackRequest: pendingRequest,
    })
    const rejected = takebackRequest(8, 'rejected')

    expect(mergeTakebackRequestChange(current, rejected)).toMatchObject({
      negotiationVersion: 8,
      drawOffer: null,
      takebackRequest: null,
      canRequestTakeback: false,
    })
  })

  it('preserves a complete game view when same-revision terminal notifications arrive later', () => {
    const pending = snapshot(12, {
      negotiationVersion: 8,
      takebackRequest: takebackRequest(8),
    })
    const moved = snapshot(13, {
      sideToMove: 'black',
      negotiationVersion: 8,
      canRequestTakeback: true,
    })
    const authoritative = mergeGameView(pending, moved)

    const afterTakeback = mergeTakebackRequestChange(
      authoritative,
      takebackRequest(8, 'withdrawn'),
    )
    const afterDraw = mergeDrawOfferChange(authoritative, drawOffer(8, 'withdrawn'))

    expect(afterTakeback).toBe(authoritative)
    expect(afterDraw).toBe(authoritative)
    expect(afterTakeback).toMatchObject({
      sideToMove: 'black',
      negotiationVersion: 8,
      drawOffer: null,
      takebackRequest: null,
      canRequestTakeback: true,
    })
  })

  it('does nothing when a notification arrives before the game snapshot', () => {
    expect(mergeDrawOfferChange(undefined, drawOffer(1))).toBeUndefined()
    expect(mergeTakebackRequestChange(undefined, takebackRequest(1))).toBeUndefined()
  })
})
