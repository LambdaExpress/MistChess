import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError, api } from '../api/client'
import { queryKeys } from '../api/queryKeys'
import type { GameOptions, GuestSession, MatchTicket } from '../api/types'
import { HomePage } from './HomePage'

const session: GuestSession = {
  playerId: 'player-1',
  displayName: '游客甲',
  activeGameId: null,
}

const ticket: MatchTicket = {
  ticketId: 'ticket-1',
  ruleVersion: 'fog-xiangqi-v1',
  timeControl: '600+5',
  status: 'searching',
  createdAt: '2026-07-26T00:00:00Z',
  lastHeartbeatAt: '2026-07-26T00:00:00Z',
  expiresAt: '2026-07-26T00:01:00Z',
  gameId: null,
}

const gameOptions: GameOptions = {
  ruleVersion: 'fog-xiangqi-v1',
  quickMatchTimeControl: {
    id: '600+5',
    label: '10 分钟 + 5 秒',
    initialSeconds: 600,
    incrementSeconds: 5,
  },
  roomTimeControls: [
    {
      id: '180+2',
      label: '3 分钟 + 2 秒',
      initialSeconds: 180,
      incrementSeconds: 2,
    },
  ],
  defaultRoomTimeControlId: '180+2',
  allowUntimedRooms: true,
  quickMatchMoveTimeLimitSeconds: 90,
  roomMoveTimeLimits: [{ seconds: 90, label: '90 秒' }],
  defaultRoomMoveTimeLimitSeconds: 90,
}

function renderHomePage() {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: Infinity },
      mutations: { retry: false },
    },
  })

  const view = render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/']}>
        <Routes>
          <Route path="/" element={<HomePage />} />
          <Route path="/match" element={<p>Matching</p>} />
          <Route path="/game/:gameId" element={<p>Active game</p>} />
          <Route path="/room/:code" element={<p>Private room</p>} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  )

  return { ...view, queryClient }
}

beforeEach(() => {
  sessionStorage.clear()
  vi.spyOn(api, 'getGameOptions').mockResolvedValue(gameOptions)
  vi.spyOn(api, 'startGuestSession').mockResolvedValue(session)
})

afterEach(() => {
  vi.restoreAllMocks()
  sessionStorage.clear()
})

describe('HomePage quick match recovery', () => {
  it('reuses the saved client request ID after a lost response and remount', async () => {
    const createMatchTicket = vi
      .spyOn(api, 'createMatchTicket')
      .mockRejectedValueOnce(new TypeError('Failed to fetch'))
      .mockResolvedValueOnce(ticket)

    const firstView = renderHomePage()
    const firstMatchButton = screen.getByRole('button', { name: '寻找对手' })
    await waitFor(() => expect(firstMatchButton).toBeEnabled())
    fireEvent.click(firstMatchButton)

    expect(await screen.findByRole('alert')).toHaveTextContent('Failed to fetch')
    expect(createMatchTicket).toHaveBeenCalledOnce()
    const firstRequestId = createMatchTicket.mock.calls[0][0]
    expect(sessionStorage.length).toBe(1)
    expect(sessionStorage.getItem(sessionStorage.key(0)!)).toBe(firstRequestId)

    firstView.unmount()
    const secondView = renderHomePage()
    const secondMatchButton = screen.getByRole('button', { name: '寻找对手' })
    await waitFor(() => expect(secondMatchButton).toBeEnabled())
    fireEvent.click(secondMatchButton)

    expect(await screen.findByText('Matching')).toBeInTheDocument()
    expect(createMatchTicket).toHaveBeenCalledTimes(2)
    expect(createMatchTicket.mock.calls[1][0]).toBe(firstRequestId)
    expect(secondView.queryClient.getQueryData(queryKeys.currentTicket)).toEqual(ticket)
    expect(sessionStorage.length).toBe(0)
  })

  it('recovers the current ticket when creation reports an active ticket', async () => {
    const activeTicketError = new ApiError(409, {
      code: 'ACTIVE_TICKET_EXISTS',
      title: 'An active ticket already exists',
    })
    vi.spyOn(api, 'createMatchTicket').mockRejectedValue(activeTicketError)
    const getCurrentMatchTicket = vi
      .spyOn(api, 'getCurrentMatchTicket')
      .mockResolvedValue(ticket)

    const { queryClient } = renderHomePage()
    const matchButton = screen.getByRole('button', { name: '寻找对手' })
    await waitFor(() => expect(matchButton).toBeEnabled())
    fireEvent.click(matchButton)

    expect(await screen.findByText('Matching')).toBeInTheDocument()
    expect(getCurrentMatchTicket).toHaveBeenCalledOnce()
    await waitFor(() => {
      expect(queryClient.getQueryData(queryKeys.currentTicket)).toEqual(ticket)
    })
    expect(sessionStorage.length).toBe(0)
  })

  it('shows a return action when the session has an active game', async () => {
    vi.mocked(api.startGuestSession).mockResolvedValue({
      ...session,
      activeGameId: 'active-game-1',
    })
    const createMatchTicket = vi.spyOn(api, 'createMatchTicket')

    renderHomePage()
    const returnButton = await screen.findByRole('button', { name: '返回对局' })
    fireEvent.click(returnButton)

    expect(await screen.findByText('Active game')).toBeInTheDocument()
    expect(createMatchTicket).not.toHaveBeenCalled()
  })

  it('resumes the unfinished game when ticket creation reports an active game', async () => {
    vi.spyOn(api, 'createMatchTicket').mockRejectedValue(
      new ApiError(409, {
        code: 'ACTIVE_GAME_EXISTS',
        title: 'The player already has an unfinished game.',
        gameId: 'active-game-1',
      }),
    )

    renderHomePage()
    const matchButton = screen.getByRole('button', { name: '寻找对手' })
    await waitFor(() => expect(matchButton).toBeEnabled())
    fireEvent.click(matchButton)

    expect(await screen.findByText('Active game')).toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })
  it('creates a timed room with the selected per-move limit', async () => {
    const createRoom = vi.spyOn(api, 'createRoom').mockResolvedValue({
      code: 'ROOM1234',
      status: 'waitingForOpponent',
      ruleVersion: 'fog-xiangqi-v1',
      timeControl: '180+2',
      players: [],
      gameId: null,
      moveTimeLimitSeconds: 90,
    })

    renderHomePage()
    const createButton = await screen.findByRole('button', { name: '创建房间' })
    await waitFor(() => expect(createButton).toBeEnabled())
    fireEvent.click(createButton)

    await waitFor(() => {
      expect(createRoom.mock.calls[0][0]).toEqual({
        timeControl: '180+2',
        moveTimeLimitSeconds: 90,
      })
    })
  })

})
