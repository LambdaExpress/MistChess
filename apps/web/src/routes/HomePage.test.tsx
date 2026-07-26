import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError, api } from '../api/client'
import { queryKeys } from '../api/queryKeys'
import type { MatchTicket } from '../api/types'
import { HomePage } from './HomePage'

const ticket: MatchTicket = {
  ticketId: 'ticket-1',
  ruleVersion: 'fog-xiangqi-v1',
  timeControl: null,
  status: 'searching',
  createdAt: '2026-07-26T00:00:00Z',
  lastHeartbeatAt: '2026-07-26T00:00:00Z',
  expiresAt: '2026-07-26T00:01:00Z',
  gameId: null,
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
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  )

  return { ...view, queryClient }
}

beforeEach(() => {
  sessionStorage.clear()
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
    fireEvent.click(screen.getByRole('button', { name: '寻找对手' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Failed to fetch')
    expect(createMatchTicket).toHaveBeenCalledOnce()
    const firstRequestId = createMatchTicket.mock.calls[0][0]
    expect(sessionStorage.length).toBe(1)
    expect(sessionStorage.getItem(sessionStorage.key(0)!)).toBe(firstRequestId)

    firstView.unmount()
    const secondView = renderHomePage()
    fireEvent.click(screen.getByRole('button', { name: '寻找对手' }))

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
    fireEvent.click(screen.getByRole('button', { name: '寻找对手' }))

    expect(await screen.findByText('Matching')).toBeInTheDocument()
    expect(getCurrentMatchTicket).toHaveBeenCalledOnce()
    await waitFor(() => {
      expect(queryClient.getQueryData(queryKeys.currentTicket)).toEqual(ticket)
    })
    expect(sessionStorage.length).toBe(0)
  })
})
