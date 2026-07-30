import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { api } from '../../api/client'
import type {
  AdminBanStatus,
  AdminHistoricalGamesPage,
  AdminUser,
  AdminUserDetail,
} from '../../api/types'
import { AdminUserDetailPage } from './AdminUserDetailPage'

const playerId = '00000000-0000-0000-0000-000000000001'
const emptyHistory: AdminHistoricalGamesPage = { games: [], nextCursor: null }

function userFixture(overrides: Partial<AdminUser> = {}): AdminUser {
  return {
    playerId,
    displayName: '待审核棋手',
    createdAt: '2026-07-26T00:00:00Z',
    expiresAt: '2026-08-26T00:00:00Z',
    lastSeenAt: '2026-07-27T00:00:00Z',
    online: true,
    banned: false,
    bannedAt: null,
    banReason: null,
    bannedBy: null,
    rating: 1500,
    gamesPlayed: 0,
    wins: 0,
    draws: 0,
    losses: 0,
    winRate: null,
    ...overrides,
  }
}

function detail(user: AdminUser): AdminUserDetail {
  return {
    user,
    ratings: [],
    observedAt: '2026-07-27T00:00:00Z',
  }
}

function renderDetail() {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: Infinity },
      mutations: { retry: false },
    },
  })

  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[`/admin/users/${playerId}`]}>
        <Routes>
          <Route path="/admin/users/:playerId" element={<AdminUserDetailPage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

afterEach(() => {
  vi.restoreAllMocks()
})

describe('AdminUserDetailPage moderation', () => {
  it('bans and unbans the selected user while preserving the empty-history experience', async () => {
    const user = userEvent.setup()
    const activeUser = userFixture()
    const bannedUser = userFixture({
      banned: true,
      online: false,
      bannedAt: '2026-07-27T00:05:00Z',
      banReason: '破坏公平对局',
      bannedBy: 'operator',
    })
    vi.spyOn(api, 'getAdminUser')
      .mockResolvedValueOnce(detail(activeUser))
      .mockResolvedValueOnce(detail(bannedUser))
      .mockResolvedValueOnce(detail(activeUser))
    vi.spyOn(api, 'getAdminUserGames').mockResolvedValue(emptyHistory)
    const banStatus: AdminBanStatus = {
      playerId,
      banned: true,
      bannedAt: '2026-07-27T00:05:00Z',
      banReason: '破坏公平对局',
      bannedBy: 'operator',
    }
    const unbanStatus: AdminBanStatus = {
      playerId,
      banned: false,
      bannedAt: null,
      banReason: null,
      bannedBy: null,
    }
    const banAdminUser = vi.spyOn(api, 'banAdminUser').mockResolvedValue(banStatus)
    const unbanAdminUser = vi.spyOn(api, 'unbanAdminUser').mockResolvedValue(unbanStatus)

    renderDetail()

    expect(await screen.findByRole('heading', { name: '待审核棋手' })).toBeInTheDocument()
    expect(await screen.findByRole('heading', { name: '该用户尚无已结束棋局' }))
      .toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: '封禁用户' }))
    expect(screen.getByRole('dialog', { name: '确认封禁 待审核棋手' })).toBeInTheDocument()
    const confirmBan = screen.getByRole('button', { name: '确认封禁' })
    expect(confirmBan).toBeDisabled()
    await user.type(screen.getByLabelText('封禁原因（1–200 字）'), '  破坏公平对局  ')
    await user.click(confirmBan)

    expect(banAdminUser).toHaveBeenCalledWith(playerId, '破坏公平对局')
    expect(await screen.findByText('用户已封禁。')).toBeInTheDocument()
    expect(await screen.findByRole('button', { name: '解除封禁' })).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: '解除封禁' }))
    expect(screen.getByRole('dialog', { name: '确认解封 待审核棋手' })).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: '确认解封' }))

    expect(unbanAdminUser).toHaveBeenCalledWith(playerId)
    expect(await screen.findByText('用户已解除封禁。')).toBeInTheDocument()
    expect(await screen.findByRole('button', { name: '封禁用户' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: '该用户尚无已结束棋局' })).toBeInTheDocument()
  })
})
