import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError, api } from '../api/client'
import { useGameHub } from '../api/hubs'
import { QUICK_MATCH_CLIENT_REQUEST_ID_KEY, type GameView, type MatchTicket } from '../api/types'
import { interpolateClock } from '../features/game/clock'
import { audioService } from '../features/audio/audioService'
import { GamePage } from './GamePage'

vi.mock('../api/hubs', () => ({
  useGameHub: vi.fn(() => 'connected'),
}))

function snapshot(overrides: Partial<GameView> = {}): GameView {
  return {
    gameId: 'game-1',
    ruleVersion: 'fog-xiangqi-v1',
    timeControl: null,
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
    ratingChange: null,
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
          <Route path="/match" element={<p>Matching</p>} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

type GameHubHandlers = Parameters<typeof useGameHub>[0]
let gameHubHandlers: GameHubHandlers | undefined

beforeEach(() => {
  sessionStorage.clear()
  gameHubHandlers = undefined
  vi.mocked(useGameHub).mockImplementation((handlers) => {
    gameHubHandlers = handlers
    return 'connected'
  })
})

afterEach(() => {
  vi.restoreAllMocks()
  sessionStorage.clear()
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

  it('refetches and recalibrates after the page becomes visible', async () => {
    const current = snapshot({
      clock: {
        redMilliseconds: 40_000,
        blackMilliseconds: 30_000,
        serverTime: '2026-07-26T00:00:00Z',
      },
    })
    const recalibrated = snapshot({
      clock: {
        redMilliseconds: 33_000,
        blackMilliseconds: 30_000,
        serverTime: '2026-07-26T00:00:07Z',
      },
    })
    const getGame = vi
      .spyOn(api, 'getGame')
      .mockResolvedValueOnce(current)
      .mockResolvedValueOnce(recalibrated)

    renderGamePage()
    await screen.findByText('00:40')
    Object.defineProperty(document, 'visibilityState', {
      configurable: true,
      value: 'visible',
    })
    act(() => {
      document.dispatchEvent(new Event('visibilitychange'))
    })

    expect(await screen.findByText('00:33')).toBeInTheDocument()
    expect(getGame).toHaveBeenCalledTimes(2)
  })

  it('plays each low-clock threshold only once after recalibration', async () => {
    const initial = snapshot({
      clock: {
        redMilliseconds: 10_100,
        blackMilliseconds: 30_000,
        serverTime: '2026-07-26T00:00:00Z',
      },
    })
    vi.spyOn(api, 'getGame').mockResolvedValue(initial)
    const emit = vi.spyOn(audioService, 'emit')

    renderGamePage()
    await screen.findByRole('heading', { name: '轮到你行棋' })
    await waitFor(() => {
      expect(emit.mock.calls.filter((call) => call[2] === 'clock-low')).toHaveLength(1)
    }, { timeout: 2_000 })

    act(() => {
      gameHubHandlers?.onView(snapshot({
        version: 9,
        clock: {
          redMilliseconds: 10_100,
          blackMilliseconds: 30_000,
          serverTime: '2026-07-26T00:00:01Z',
        },
      }))
    })
    const delay = Promise.withResolvers<void>()
    window.setTimeout(delay.resolve, 500)
    await delay.promise

    expect(emit.mock.calls.filter((call) => call[2] === 'clock-low')).toHaveLength(1)
  })

  it('allows only one game command until the in-flight command settles', async () => {
    const current = snapshot()
    const {
      promise: resignResponse,
      resolve: resolveResign,
    } = Promise.withResolvers<GameView>()
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

  it('recovers an active ticket when a result-page rematch response was lost', async () => {
    const finished = snapshot({
      status: 'finished',
      result: { winner: 'black', reason: 'resignation' },
      candidateMoves: [],
      ratingChange: { before: 1500, after: 1480, delta: -20 },
    })
    const ticket: MatchTicket = {
      ticketId: 'ticket-rematch',
      ruleVersion: 'fog-xiangqi-v1',
      timeControl: '600+5',
      status: 'searching',
      createdAt: '2026-07-27T00:00:00Z',
      lastHeartbeatAt: '2026-07-27T00:00:00Z',
      expiresAt: '2026-07-27T00:01:00Z',
      gameId: null,
    }
    vi.spyOn(api, 'getGame').mockResolvedValue(finished)
    const createMatchTicket = vi.spyOn(api, 'createMatchTicket').mockRejectedValue(
      new ApiError(409, {
        code: 'ACTIVE_TICKET_EXISTS',
        title: 'An active ticket already exists',
      }),
    )
    const getCurrentMatchTicket = vi
      .spyOn(api, 'getCurrentMatchTicket')
      .mockResolvedValue(ticket)

    renderGamePage()
    fireEvent.click(await screen.findByRole('button', { name: '重新匹配' }))

    expect(await screen.findByText('Matching')).toBeInTheDocument()
    expect(createMatchTicket).toHaveBeenCalledOnce()
    expect(getCurrentMatchTicket).toHaveBeenCalledOnce()
    expect(sessionStorage.getItem(QUICK_MATCH_CLIENT_REQUEST_ID_KEY)).toBeNull()
  })
})

describe('interpolateClock', () => {
  it('decrements only the active side from a monotonic snapshot', () => {
    expect(interpolateClock(10_000, 9_000, 'red', true, 1_200)).toEqual({
      redMilliseconds: 8_800,
      blackMilliseconds: 9_000,
    })
    expect(interpolateClock(10_000, 9_000, 'black', true, 1_200)).toEqual({
      redMilliseconds: 10_000,
      blackMilliseconds: 7_800,
    })
  })

  it('freezes finished games and clamps expired time at zero', () => {
    expect(interpolateClock(800, 900, 'red', false, 10_000)).toEqual({
      redMilliseconds: 800,
      blackMilliseconds: 900,
    })
    expect(interpolateClock(800, 900, 'red', true, 10_000)).toEqual({
      redMilliseconds: 0,
      blackMilliseconds: 900,
    })
  })
})
