import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { api } from '../../api/client'
import type {
  AdminSession,
  AdminUser,
  AdminUsersPage as AdminUsersPageData,
} from '../../api/types'
import { AdminLayout } from '../../features/admin/AdminLayout'
import { AdminUsersPage } from './AdminUsersPage'

const observedAt = '2026-07-27T00:00:00Z'
const adminSession: AdminSession = {
  username: 'operator',
  expiresAt: '2099-07-27T08:00:00Z',
}

function userFixture(overrides: Partial<AdminUser> = {}): AdminUser {
  return {
    playerId: '00000000-0000-0000-0000-000000000001',
    displayName: '棋手一号',
    createdAt: '2026-07-26T00:00:00Z',
    expiresAt: '2026-08-26T00:00:00Z',
    lastSeenAt: '2026-07-27T00:00:00Z',
    online: false,
    banned: false,
    bannedAt: null,
    banReason: null,
    bannedBy: null,
    rating: 1510,
    gamesPlayed: 4,
    wins: 2,
    draws: 1,
    losses: 1,
    winRate: 50,
    ...overrides,
  }
}

function page(items: AdminUser[], nextCursor: string | null = null): AdminUsersPageData {
  return { items, nextCursor, observedAt }
}

function renderUsers(initialEntry = '/admin/users', includeLayout = false) {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: Infinity },
      mutations: { retry: false },
    },
  })

  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[initialEntry]}>
        <Routes>
          {includeLayout ? (
            <Route path="/admin" element={<AdminLayout />}>
              <Route path="users" element={<AdminUsersPage />} />
            </Route>
          ) : (
            <Route path="/admin/users" element={<AdminUsersPage />} />
          )}
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

afterEach(() => {
  vi.restoreAllMocks()
})

describe('AdminUsersPage', () => {
  it('moves forward and backward through cursor pages', async () => {
    const user = userEvent.setup()
    const firstUser = userFixture({ displayName: '第一页棋手' })
    const secondUser = userFixture({
      playerId: '00000000-0000-0000-0000-000000000002',
      displayName: '第二页棋手',
    })
    vi.spyOn(api, 'getAdminUsers').mockImplementation(({ cursor }) =>
      Promise.resolve(cursor === 'next-page'
        ? page([secondUser])
        : page([firstUser], 'next-page')))

    renderUsers()

    expect(await screen.findByText('第一页棋手')).toBeInTheDocument()
    expect(screen.getByText('第 1 页')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '上一页' })).toBeDisabled()

    await user.click(screen.getByRole('button', { name: '下一页' }))
    expect(await screen.findByText('第二页棋手')).toBeInTheDocument()
    expect(screen.getByText('第 2 页')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '下一页' })).toBeDisabled()

    await user.click(screen.getByRole('button', { name: '上一页' }))
    expect(await screen.findByText('第一页棋手')).toBeInTheDocument()
    expect(screen.getByText('第 1 页')).toBeInTheDocument()
  })

  it('shows a useful empty result and lets the administrator clear filters', async () => {
    const user = userEvent.setup()
    vi.spyOn(api, 'getAdminUsers').mockImplementation(({ query }) =>
      Promise.resolve(query ? page([]) : page([userFixture()])))

    renderUsers('/admin/users?query=missing')

    expect(await screen.findByRole('heading', { name: '没有符合条件的用户' }))
      .toBeInTheDocument()
    expect(screen.getByText('尝试缩短搜索内容或放宽封禁状态筛选。')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: '清除筛选' }))
    expect(await screen.findByText('棋手一号')).toBeInTheDocument()
  })

  it('offers an online-user entry that switches to the online directory', async () => {
    const user = userEvent.setup()
    vi.spyOn(api, 'getAdminSession').mockResolvedValue(adminSession)
    const offlineUser = userFixture({ displayName: '离线棋手' })
    const onlineUser = userFixture({
      playerId: '00000000-0000-0000-0000-000000000003',
      displayName: '在线棋手',
      online: true,
    })
    vi.spyOn(api, 'getAdminUsers').mockImplementation(({ online }) =>
      Promise.resolve(online === 'online' ? page([onlineUser]) : page([offlineUser])))

    renderUsers('/admin/users', true)
    expect(await screen.findByText('离线棋手')).toBeInTheDocument()

    await user.click(screen.getByRole('link', { name: '当前在线' }))

    expect(await screen.findByRole('heading', { name: '当前在线用户' })).toBeInTheDocument()
    expect(await screen.findByText('在线棋手')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: '当前在线' })).toHaveAttribute('aria-current', 'page')
  })
})
