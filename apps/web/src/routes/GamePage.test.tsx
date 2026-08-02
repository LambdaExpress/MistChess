import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError, api } from '../api/client'
import { useGameHub } from '../api/hubs'
import { queryKeys } from '../api/queryKeys'
import {
  QUICK_MATCH_CLIENT_REQUEST_ID_KEY,
  type GameView,
  type GuestSession,
  type MatchTicket,
  type TakebackRequestView,
} from '../api/types'
import { interpolateClock } from '../features/game/clock'
import { audioService } from '../features/audio/audioService'
import { GamePage } from './GamePage'

vi.mock('../api/hubs', () => ({
  useGameHub: vi.fn(() => 'connected'),
}))
const session: GuestSession = {
  playerId: 'player-1',
  displayName: '游客甲',
  activeGameId: null,
}


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
    negotiationVersion: 0,
    takebackRequest: null,
    lastAction: null,
    canRequestTakeback: false,
    ...overrides,
  }
}

function pendingTakeback(
  overrides: Partial<TakebackRequestView> = {},
): TakebackRequestView {
  return {
    id: 'takeback-1',
    status: 'pending',
    requestedBy: 'black',
    requestedPly: 3,
    requestedAtVersion: 8,
    resolvedAtVersion: null,
    createdAt: '2026-07-31T00:00:00Z',
    revision: 1,
    ...overrides,
  }
}

function renderGamePage() {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: Infinity },
      mutations: { retry: false, gcTime: 0 },
    },
  })
  queryClient.setQueryData(queryKeys.session, session)

  const view = render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/game/game-1']}>
        <Routes>
          <Route path="/game/:gameId" element={<GamePage />} />
          <Route path="/match" element={<p>Matching</p>} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  )

  return { ...view, queryClient }
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
  it('keeps the session active game in sync with the loaded game state', async () => {
    vi.spyOn(api, 'getGame').mockResolvedValue(snapshot())

    const { queryClient } = renderGamePage()
    await screen.findByRole('heading', { name: '轮到你行棋' })
    await waitFor(() => {
      expect(queryClient.getQueryData<GuestSession>(queryKeys.session)?.activeGameId)
        .toBe('game-1')
    })

    act(() => {
      gameHubHandlers?.onView(snapshot({
        version: 9,
        status: 'finished',
        result: { winner: 'black', reason: 'resignation' },
        candidateMoves: [],
      }))
    })

    await waitFor(() => {
      expect(queryClient.getQueryData<GuestSession>(queryKeys.session)?.activeGameId)
        .toBeNull()
    })
  })

  it('recovers a missed equal-version draw offer through the HTTP refetch callback', async () => {
    const current = snapshot()
    const recovered = snapshot({
      drawOffer: {
        id: 'draw-recovered',
        offeredBy: 'black',
        status: 'pending',
        revision: 1,
      },
      negotiationVersion: 1,
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

    expect(await screen.findByRole('alertdialog', { name: '对手提议和棋' }))
      .toBeInTheDocument()
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

  it('emits both players authoritative moves once and preserves capture-terminal order', async () => {
    vi.spyOn(api, 'getGame').mockResolvedValue(snapshot())
    const emitLive = vi.spyOn(audioService, 'emitLive')

    renderGamePage()
    await screen.findByRole('heading', { name: '轮到你行棋' })
    expect(emitLive).not.toHaveBeenCalled()

    const ownMove = snapshot({
      version: 9,
      sideToMove: 'black',
      lastAction: { version: 9, kind: 'move', actor: 'red' },
    })
    act(() => {
      gameHubHandlers?.onView(ownMove)
      gameHubHandlers?.onView(ownMove)
      gameHubHandlers?.onSnapshot?.(ownMove)
    })
    await waitFor(() => {
      expect(emitLive).toHaveBeenCalledWith('game-1', 9, ['move-self'])
    })
    expect(emitLive).toHaveBeenCalledTimes(1)
    emitLive.mockClear()

    act(() => {
      gameHubHandlers?.onView(snapshot({
        version: 10,
        sideToMove: 'red',
        lastAction: { version: 10, kind: 'move', actor: 'black' },
      }))
    })
    await waitFor(() => {
      expect(emitLive).toHaveBeenCalledWith('game-1', 10, ['move-opponent'])
    })
    emitLive.mockClear()

    act(() => {
      gameHubHandlers?.onView(snapshot({
        version: 11,
        status: 'finished',
        result: { winner: 'red', reason: 'generalCaptured' },
        sideToMove: 'black',
        captureSummary: { redLost: [], blackLost: ['general'] },
        candidateMoves: [],
        lastAction: { version: 11, kind: 'capture', actor: 'red' },
      }))
    })
    await waitFor(() => {
      expect(emitLive).toHaveBeenCalledWith('game-1', 11, ['capture', 'game-win'])
    })
    emitLive.mockClear()

    act(() => {
      gameHubHandlers?.onView(snapshot({
        version: 12,
        sideToMove: 'red',
        lastAction: { version: 12, kind: 'takebackAccepted', actor: 'black' },
      }))
    })
    expect(emitLive).not.toHaveBeenCalled()
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

  it('shows the current turn budget separately from the total clock', async () => {
    vi.spyOn(api, 'getGame').mockResolvedValue(snapshot({
      timeControl: '600+5',
      moveTimeLimitSeconds: 90,
      clock: {
        redMilliseconds: 600_000,
        blackMilliseconds: 600_000,
        serverTime: '2026-07-27T00:00:00Z',
        turnMilliseconds: 90_000,
      },
    }))

    renderGamePage()

    const ownClock = await screen.findByLabelText('我方红方计时')
    expect(within(ownClock).getByText('本步')).toBeInTheDocument()
    expect(within(ownClock).getByText('01:30')).toBeInTheDocument()
    expect(screen.getAllByText('10:00')).toHaveLength(2)
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
      .mockResolvedValue({
        id: 'draw-1',
        offeredBy: 'red',
        status: 'pending',
        revision: 1,
      })

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
    const waitingDraw = await screen.findByRole('status', { name: '已提议和棋' })
    expect(waitingDraw.closest('.game-board-stage')).not.toBeNull()
    expect(within(waitingDraw).queryByRole('button')).not.toBeInTheDocument()
  })

  it('shows incoming draw and takeback requests in the board overlay', async () => {
    vi.spyOn(api, 'getGame').mockResolvedValue(snapshot())

    const { container } = renderGamePage()
    await screen.findByRole('heading', { name: '轮到你行棋' })

    act(() => {
      gameHubHandlers?.onDrawOffer({
        id: 'draw-1',
        offeredBy: 'black',
        status: 'pending',
        revision: 1,
      })
    })
    const drawDialog = await screen.findByRole('alertdialog', {
      name: '对手提议和棋',
    })
    expect(drawDialog.closest('.game-board-stage')).not.toBeNull()
    expect(screen.getByRole('button', { name: '同意' })).toHaveFocus()

    act(() => {
      gameHubHandlers?.onDrawOffer({
        id: 'draw-1',
        offeredBy: 'black',
        status: 'rejected',
        revision: 2,
      })
      gameHubHandlers?.onTakebackRequest?.(pendingTakeback({ revision: 3 }))
    })
    const takebackDialog = await screen.findByRole('alertdialog', {
      name: '对手请求悔棋',
    })
    expect(takebackDialog.closest('.game-board-stage')).not.toBeNull()
    expect(takebackDialog).toHaveTextContent('对手请求撤销第 3 手')
    expect(screen.getByRole('button', { name: '同意' })).toHaveFocus()

    act(() => {
      gameHubHandlers?.onTakebackRequest?.(pendingTakeback({
        status: 'rejected',
        revision: 4,
        resolvedAtVersion: 8,
      }))
      gameHubHandlers?.onTakebackRequest?.(pendingTakeback({
        id: 'takeback-own',
        requestedBy: 'red',
        revision: 5,
      }))
    })
    const waiting = await screen.findByRole('status', { name: '已请求悔棋' })
    expect(waiting.closest('.game-board-stage')).not.toBeNull()
    expect(within(waiting).queryByRole('button')).not.toBeInTheDocument()
    expect(container.querySelectorAll('.game-negotiation-overlay')).toHaveLength(1)
  })

  it('places the opponent clock above the board and the player clock below it', async () => {
    vi.spyOn(api, 'getGame').mockResolvedValue(snapshot({
      perspective: 'black',
      sideToMove: 'red',
      clock: {
        redMilliseconds: 120_000,
        blackMilliseconds: 180_000,
        serverTime: new Date().toISOString(),
        turnMilliseconds: 60_000,
      },
    }))

    const { container } = renderGamePage()
    const opponentClock = await screen.findByRole('region', {
      name: '对方红方计时',
    })
    const playerClock = screen.getByRole('region', { name: '我方黑方计时' })
    const boardStage = container.querySelector('.game-board-stage')

    expect(boardStage).not.toBeNull()
    expect(opponentClock.compareDocumentPosition(boardStage!) & Node.DOCUMENT_POSITION_FOLLOWING)
      .toBeTruthy()
    expect(boardStage!.compareDocumentPosition(playerClock) & Node.DOCUMENT_POSITION_FOLLOWING)
      .toBeTruthy()
    expect(within(opponentClock).getByText('02:00')).toBeInTheDocument()
    expect(within(playerClock).getByText('03:00')).toBeInTheDocument()
  })

  it('offers takeback only when the authoritative view grants eligibility', async () => {
    vi.spyOn(api, 'getGame').mockResolvedValue(snapshot())
    const createTakebackRequest = vi.spyOn(api, 'createTakebackRequest')
      .mockResolvedValue(pendingTakeback({
        requestedBy: 'red',
        requestedAtVersion: 9,
        revision: 1,
      }))

    renderGamePage()
    await screen.findByRole('heading', { name: '轮到你行棋' })
    expect(screen.queryByRole('button', { name: '请求悔棋' })).not.toBeInTheDocument()

    act(() => {
      gameHubHandlers?.onView(snapshot({
        version: 9,
        sideToMove: 'black',
        canRequestTakeback: true,
        lastAction: { version: 9, kind: 'move', actor: 'red' },
      }))
    })
    const actionCard = screen.getByRole('heading', { name: '对局操作' }).parentElement
    expect(actionCard).toHaveClass('action-card')
    expect(within(actionCard!).getByRole('button', { name: '提议和棋' })).toBeInTheDocument()
    expect(within(actionCard!).getByRole('button', { name: '认输' })).toBeInTheDocument()
    const takebackButton = await screen.findByRole('button', { name: '请求悔棋' })
    expect(takebackButton.closest('.action-card')).toBe(actionCard)
    fireEvent.click(takebackButton)

    await waitFor(() => {
      expect(createTakebackRequest).toHaveBeenCalledWith('game-1', {
        expectedVersion: 9,
        clientRequestId: expect.any(String),
      })
    })
    expect(await screen.findByRole('status', { name: '已请求悔棋' }))
      .toBeInTheDocument()
  })

  it('does not render internal connection or version copy', async () => {
    vi.spyOn(api, 'getGame').mockResolvedValue(snapshot())

    const { container } = renderGamePage()
    await screen.findByRole('heading', { name: '轮到你行棋' })

    expect(container.querySelector('.game-connection')).not.toBeInTheDocument()
    expect(screen.queryByText(/LIVE GAME/)).not.toBeInTheDocument()
    expect(screen.queryByText(/^局面版本/)).not.toBeInTheDocument()
    expect(screen.queryByText('实时同步')).not.toBeInTheDocument()
  })

  it('recovers an active ticket when a result-page rematch response was lost', async () => {
    const finished = snapshot({
      status: 'finished',
      result: { winner: 'black', reason: 'resignation' },
      candidateMoves: [],
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
