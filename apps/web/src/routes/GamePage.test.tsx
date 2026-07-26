import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from '../api/client'
import { useGameHub } from '../api/hubs'
import type { GameView } from '../api/types'
import { GamePage } from './GamePage'

vi.mock('../api/hubs', () => ({
  useGameHub: vi.fn(() => 'connected'),
}))

function snapshot(overrides: Partial<GameView> = {}): GameView {
  return {
    gameId: 'game-1',
    ruleVersion: 'fog-xiangqi-v1',
    version: 8,
    status: 'playing',
    result: null,
    perspective: 'red',
    sideToMove: 'red',
    visibleSquares: [
      { file: 0, rank: 0 },
      { file: 0, rank: 1 },
    ],
    pieces: [{ side: 'red', type: 'rook', position: { file: 0, rank: 0 } }],
    candidateMoves: [
      { from: { file: 0, rank: 0 }, destinations: [{ file: 0, rank: 1 }] },
    ],
    captureSummary: { redLost: [], blackLost: [] },
    clock: null,
    drawOffer: null,
    ...overrides,
  }
}

function renderGamePage() {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false, gcTime: 0 },
    },
  })

  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/game/game-1']}>
        <Routes>
          <Route path="/game/:gameId" element={<GamePage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

type GameHubHandlers = Parameters<typeof useGameHub>[0]
let gameHubHandlers: GameHubHandlers | undefined

beforeEach(() => {
  gameHubHandlers = undefined
  vi.mocked(useGameHub).mockImplementation((handlers) => {
    gameHubHandlers = handlers
    return 'connected'
  })
})

afterEach(() => {
  vi.restoreAllMocks()
})

describe('GamePage recovery and commands', () => {
  it('recovers a missed equal-version draw offer through the HTTP refetch callback', async () => {
    const current = snapshot()
    const recovered = snapshot({
      drawOffer: { offeredBy: 'black', status: 'pending' },
      clock: {
        redMilliseconds: 42_000,
        blackMilliseconds: 21_000,
        serverTime: '2026-07-26T00:00:00Z',
      },
    })
    const getGame = vi
      .spyOn(api, 'getGame')
      .mockResolvedValueOnce(current)
      .mockResolvedValueOnce(recovered)

    renderGamePage()
    await screen.findByRole('heading', { name: '轮到你行棋' })

    act(() => {
      gameHubHandlers?.onReconnect()
    })

    expect(await screen.findByRole('alert')).toHaveTextContent('对手提议和棋')
    expect(screen.getByText('00:42')).toBeInTheDocument()
    expect(screen.getByText('00:21')).toBeInTheDocument()
    expect(getGame).toHaveBeenCalledTimes(2)
  })

  it('allows only one game command until the in-flight command settles', async () => {
    const current = snapshot()
    let resolveResign!: (view: GameView) => void
    const resignResponse = new Promise<GameView>((resolve) => {
      resolveResign = resolve
    })
    vi.spyOn(api, 'getGame').mockResolvedValue(current)
    const resign = vi.spyOn(api, 'resignGame').mockReturnValue(resignResponse)
    const offerDraw = vi
      .spyOn(api, 'offerDraw')
      .mockResolvedValue({ offeredBy: 'red', status: 'pending' })

    renderGamePage()
    await screen.findByRole('heading', { name: '轮到你行棋' })
    fireEvent.click(screen.getByRole('button', { name: '认输' }))

    const confirmResign = screen.getByRole('button', { name: '确认认输' })
    const drawButton = screen.getByRole('button', { name: '提议和棋' })
    act(() => {
      confirmResign.click()
      drawButton.click()
    })

    await waitFor(() => expect(resign).toHaveBeenCalledOnce())
    expect(offerDraw).not.toHaveBeenCalled()
    expect(screen.getByTestId('game-board')).toHaveAttribute('aria-busy', 'true')

    await act(async () => {
      resolveResign(current)
      await resignResponse
    })
    await waitFor(() => expect(drawButton).toBeEnabled())

    fireEvent.click(drawButton)
    await waitFor(() => expect(offerDraw).toHaveBeenCalledOnce())
  })
})
