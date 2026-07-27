import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from '../api/client'
import type { GameOptions, GuestSession, HistoricalGame } from '../api/types'
import { HistoryPage } from './HistoryPage'

const session: GuestSession = {
  playerId: 'player-1',
  displayName: '游客甲',
  activeGameId: null,
}

const options: GameOptions = {
  ruleVersion: 'fog-xiangqi-v1',
  quickMatchTimeControl: {
    id: '600+5',
    label: '10 分钟 + 5 秒',
    initialSeconds: 600,
    incrementSeconds: 5,
  },
  roomTimeControls: [
    { id: '600+5', label: '10 分钟 + 5 秒', initialSeconds: 600, incrementSeconds: 5 },
  ],
  defaultRoomTimeControlId: '600+5',
  allowUntimedRooms: true,
  quickMatchMoveTimeLimitSeconds: 90,
  roomMoveTimeLimits: [{ seconds: 90, label: '90 秒' }],
  defaultRoomMoveTimeLimitSeconds: 90,
}

function game(gameId: string, currentPlayerSide: 'red' | 'black'): HistoricalGame {
  return {
    gameId,
    finishedAt: '2026-07-27T01:02:03Z',
    ruleVersion: 'fog-xiangqi-v1',
    timeControl: '600+5',
    currentPlayerSide,
    red: { displayName: '游客甲', outcome: 'win' },
    black: { displayName: '游客乙', outcome: 'loss' },
    result: { winner: 'red', reason: 'resignation' },
    plyCount: 12,
  }
}

function renderHistoryPage() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
  })
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/history']}>
        <Routes>
          <Route path="/history" element={<HistoryPage />} />
          <Route path="/history/:gameId" element={<p>Replay destination</p>} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  vi.spyOn(api, 'startGuestSession').mockResolvedValue(session)
  vi.spyOn(api, 'getGameOptions').mockResolvedValue(options)
})

afterEach(() => {
  vi.restoreAllMocks()
})

describe('HistoryPage', () => {
  it('renders private results, filters, pagination, and whole-row replay links', async () => {
    const firstGame = game('game-1', 'red')
    const secondGame = game('game-2', 'black')
    const getHistory = vi.spyOn(api, 'getGameHistory').mockImplementation(async (request) => {
      if (request.result === 'loss') return { games: [secondGame], nextCursor: null }
      if (request.cursor === 'next-page') return { games: [secondGame], nextCursor: null }
      return { games: [firstGame], nextCursor: 'next-page' }
    })

    renderHistoryPage()

    const firstRow = await screen.findByRole('link', { name: /游客甲.*游客乙.*查看回放/s })
    expect(firstRow).toHaveAttribute('href', '/history/game-1')
    expect(screen.getAllByText('10 分钟 + 5 秒')).toHaveLength(2)
    expect(screen.getAllByText('我')).toHaveLength(1)

    fireEvent.click(screen.getByRole('button', { name: '加载更多' }))
    await waitFor(() => expect(screen.getAllByText('查看回放 ›')).toHaveLength(2))
    expect(getHistory).toHaveBeenCalledWith(expect.objectContaining({ cursor: 'next-page', limit: 20 }))

    fireEvent.change(screen.getByLabelText('我的结果'), { target: { value: 'loss' } })
    await waitFor(() => {
      expect(getHistory).toHaveBeenCalledWith(expect.objectContaining({ result: 'loss' }))
    })
  })
})
