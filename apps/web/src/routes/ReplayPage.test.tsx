import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from '../api/client'
import { queryKeys } from '../api/queryKeys'
import type { GuestSession, HistoricalReplay, ReplayProjection } from '../api/types'
import { ReplayPage } from './ReplayPage'

const session: GuestSession = {
  playerId: 'player-1',
  displayName: '游客甲',
  rating: { ruleVersion: 'fog-xiangqi-v1', timeControl: '600+5', rating: 1512, gamesPlayed: 3 },
}
const allPositions = Array.from({ length: 90 }, (_, index) => ({
  file: index % 9,
  rank: Math.floor(index / 9),
}))
const redMove = {
  ply: 1,
  side: 'red' as const,
  piece: 'rook' as const,
  from: { file: 0, rank: 0 },
  to: { file: 0, rank: 1 },
  captured: null,
}

function projection(
  visibleSquares: { file: number; rank: number }[],
  move: ReplayProjection['move'],
): ReplayProjection {
  return {
    visibleSquares,
    pieces: [
      { side: 'red', type: 'rook', position: { file: 0, rank: 1 } },
      { side: 'black', type: 'rook', position: { file: 8, rank: 9 } },
    ],
    captureSummary: { redLost: [], blackLost: [] },
    move,
  }
}

function replay(currentPlayerSide: 'red' | 'black' | null): HistoricalReplay {
  const initial = projection(allPositions, null)
  return {
    gameId: 'game-1',
    ruleVersion: 'fog-xiangqi-v1',
    timeControl: '600+5',
    currentPlayerSide,
    red: { displayName: '游客甲', outcome: 'win', ratingBefore: 1500 },
    black: { displayName: '游客乙', outcome: 'loss', ratingBefore: 1500 },
    result: { winner: 'red', reason: 'resignation' },
    frames: [
      {
        ply: 0,
        sideToMove: 'red',
        clock: { redMilliseconds: 600_000, blackMilliseconds: 600_000, serverTime: '2026-07-27T00:00:00Z' },
        views: { red: initial, black: initial, omniscient: initial },
      },
      {
        ply: 1,
        sideToMove: 'black',
        clock: { redMilliseconds: 604_000, blackMilliseconds: 600_000, serverTime: '2026-07-27T00:00:01Z' },
        views: {
          red: projection([{ file: 0, rank: 1 }], redMove),
          black: projection([{ file: 8, rank: 9 }], null),
          omniscient: projection(allPositions, redMove),
        },
      },
      {
        ply: 1,
        sideToMove: 'black',
        clock: { redMilliseconds: 604_000, blackMilliseconds: 600_000, serverTime: '2026-07-27T00:00:02Z' },
        views: {
          red: projection([{ file: 0, rank: 1 }], null),
          black: projection([{ file: 8, rank: 9 }], null),
          omniscient: projection(allPositions, null),
        },
      },
    ],
  }
}

function renderReplayPage(shared = false) {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: Infinity },
      mutations: { retry: false },
    },
  })
  if (!shared) queryClient.setQueryData(queryKeys.session, session)
  const route = shared ? '/shared/replay/secret-token' : '/history/game-1'
  const view = render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[route]}>
        <Routes>
          <Route path="/history/:gameId" element={<ReplayPage />} />
          <Route path="/shared/replay/:shareToken" element={<ReplayPage shared />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  )
  return { ...view, queryClient }
}

beforeEach(() => {
  Object.defineProperty(navigator, 'clipboard', {
    configurable: true,
    value: { writeText: vi.fn().mockResolvedValue(undefined) },
  })
})

afterEach(() => {
  vi.restoreAllMocks()
})

describe('ReplayPage', () => {
  it('switches cached views without refetching or resetting the current frame', async () => {
    const getReplay = vi.spyOn(api, 'getReplay').mockResolvedValue(replay('red'))
    vi.spyOn(api, 'createReplayShare').mockResolvedValue({
      sharePath: '/shared/replay/share-token',
      createdAt: '2026-07-27T00:00:00Z',
    })
    vi.spyOn(api, 'revokeReplayShare').mockResolvedValue(undefined)

    renderReplayPage()
    await screen.findByRole('heading', { name: '迷雾棋局回放' })
    expect(screen.getByRole('button', { name: '红方视野' })).toHaveAttribute('aria-pressed', 'true')

    fireEvent.click(screen.getByRole('button', { name: '下一步' }))
    expect(await screen.findByText('红方车')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: '黑方视野' }))
    expect(screen.getByText('对手走子或终局事件')).toBeInTheDocument()
    expect(screen.getByText('1', { selector: '.replay-counter > strong' })).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: '全局视野' }))
    expect(screen.getByText('红方车')).toBeInTheDocument()
    expect(getReplay).toHaveBeenCalledOnce()

    fireEvent.click(screen.getByRole('button', { name: '生成分享链接' }))
    const shareInput = await screen.findByRole('textbox', { name: '分享链接' })
    expect(shareInput).toHaveValue('http://localhost:3000/shared/replay/share-token')
    fireEvent.click(screen.getByRole('button', { name: '复制链接' }))
    await waitFor(() => expect(navigator.clipboard.writeText).toHaveBeenCalledWith(
      'http://localhost:3000/shared/replay/share-token',
    ))
    fireEvent.click(screen.getByRole('button', { name: '撤销当前分享' }))
    await waitFor(() => expect(screen.queryByRole('textbox', { name: '分享链接' })).not.toBeInTheDocument())
  })

  it('loads a shared route without secure-context APIs or exposing the token in query keys', async () => {
    const getSharedReplay = vi.spyOn(api, 'getSharedReplay').mockResolvedValue(replay(null))
    const getReplay = vi.spyOn(api, 'getReplay')
    const originalSubtle = crypto.subtle
    Object.defineProperty(crypto, 'subtle', {
      configurable: true,
      value: undefined,
    })

    try {
      const { queryClient } = renderReplayPage(true)
      await screen.findByRole('heading', { name: '迷雾棋局回放' })

      expect(getSharedReplay).toHaveBeenCalledWith('secret-token')
      expect(getReplay).not.toHaveBeenCalled()
      expect(screen.getByText(/通过分享链接观看。此链接只授予/)).toBeInTheDocument()
      expect(screen.queryByText('分享这局回放')).not.toBeInTheDocument()
      expect(JSON.stringify(queryClient.getQueryCache().getAll().map((query) => query.queryKey)))
        .not.toContain('secret-token')
    } finally {
      Object.defineProperty(crypto, 'subtle', {
        configurable: true,
        value: originalSubtle,
      })
    }
  })
})
