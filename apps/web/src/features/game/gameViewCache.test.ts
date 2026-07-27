import { describe, expect, it } from 'vitest'
import type { GameView } from '../../api/types'
import {
  replaceWithAuthoritativeGameView,
  replaceWithNewerGameView,
} from './gameViewCache'

function snapshot(version: number): GameView {
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
    ratingChange: null,
  }
}

describe('replaceWithAuthoritativeGameView', () => {
  it('replaces equal-version HTTP snapshots so mutable state can recover', () => {
    const current = snapshot(8)
    current.drawOffer = null
    const incoming = snapshot(8)
    incoming.drawOffer = { offeredBy: 'black', status: 'pending' }
    incoming.clock = {
      redMilliseconds: 42_000,
      blackMilliseconds: 21_000,
      serverTime: '2026-07-26T00:00:00Z',
    }

    expect(replaceWithAuthoritativeGameView(current, incoming)).toBe(incoming)
  })

  it('does not let an older HTTP response roll back a newer snapshot', () => {
    const current = snapshot(8)
    expect(replaceWithAuthoritativeGameView(current, snapshot(7))).toBe(current)
  })
})

describe('replaceWithNewerGameView', () => {
  it('discards stale and duplicate SignalR snapshots', () => {
    const current = snapshot(8)
    expect(replaceWithNewerGameView(current, snapshot(7))).toBe(current)
    expect(replaceWithNewerGameView(current, snapshot(8))).toBe(current)
  })

  it('fully replaces the cache with a newer authoritative snapshot', () => {
    const current = snapshot(8)
    const incoming = snapshot(9)
    incoming.sideToMove = 'black'

    expect(replaceWithNewerGameView(current, incoming)).toBe(incoming)
  })
})
