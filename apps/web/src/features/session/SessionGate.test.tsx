import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { ApiError, api } from '../../api/client'
import type { GuestSession } from '../../api/types'
import { SessionGate } from './SessionGate'

const session: GuestSession = {
  playerId: '00000000-0000-0000-0000-000000000001',
  displayName: '游客一号',
  activeGameId: null,
}

function renderSessionGate() {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: Infinity },
      mutations: { retry: false },
    },
  })

  return render(
    <QueryClientProvider client={queryClient}>
      <SessionGate>
        <h1>玩家首页</h1>
      </SessionGate>
    </QueryClientProvider>,
  )
}

afterEach(() => {
  vi.restoreAllMocks()
})

describe('SessionGate player bans', () => {
  it('shows the dedicated ban page when session creation returns PLAYER_BANNED', async () => {
    const startGuestSession = vi.spyOn(api, 'startGuestSession').mockRejectedValue(
      new ApiError(403, {
        code: 'PLAYER_BANNED',
        title: '账号已被封禁',
        detail: '该身份因恶意逃跑已被封禁。',
      }),
    )

    renderSessionGate()

    expect(await screen.findByRole('heading', { name: '账号已被封禁' })).toBeInTheDocument()
    expect(screen.getByText('该身份因恶意逃跑已被封禁。')).toBeInTheDocument()

    await act(async () => {
      window.dispatchEvent(new Event('mistchess:session-invalid'))
    })
    expect(startGuestSession).toHaveBeenCalledOnce()
  })

  it('replaces an active player view after a ban notification and blocks identity rotation', async () => {
    const startGuestSession = vi.spyOn(api, 'startGuestSession').mockResolvedValue(session)
    vi.spyOn(api, 'heartbeatSession').mockResolvedValue(undefined)

    renderSessionGate()
    expect(await screen.findByRole('heading', { name: '玩家首页' })).toBeInTheDocument()
    expect(startGuestSession).toHaveBeenCalledOnce()

    act(() => {
      window.dispatchEvent(new CustomEvent<string>('mistchess:account-banned', {
        detail: '管理员判定该身份破坏公平对局。',
      }))
    })

    expect(await screen.findByRole('heading', { name: '账号已被封禁' })).toBeInTheDocument()
    expect(screen.getByText('管理员判定该身份破坏公平对局。')).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: '玩家首页' })).not.toBeInTheDocument()

    await act(async () => {
      window.dispatchEvent(new Event('mistchess:session-invalid'))
    })
    expect(startGuestSession).toHaveBeenCalledOnce()
  })
})
