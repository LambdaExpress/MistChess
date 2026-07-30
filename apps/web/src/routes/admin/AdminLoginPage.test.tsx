import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { ApiError, api } from '../../api/client'
import type { AdminSession } from '../../api/types'
import { AdminGate } from '../../features/admin/AdminGate'
import { AdminLayout } from '../../features/admin/AdminLayout'
import { AdminLoginPage } from './AdminLoginPage'

const adminSession: AdminSession = {
  username: 'operator',
  expiresAt: '2099-07-27T08:00:00Z',
}

function renderAdminApp(initialEntry: string) {
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
          <Route path="/admin" element={<AdminLayout />}>
            <Route path="login" element={<AdminLoginPage />} />
            <Route element={<AdminGate />}>
              <Route path="users" element={<h1>用户目录</h1>} />
            </Route>
          </Route>
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

afterEach(() => {
  vi.restoreAllMocks()
  vi.useRealTimers()
})

describe('AdminLoginPage', () => {
  it('logs in with normalized credentials and opens the protected user directory', async () => {
    const user = userEvent.setup()
    vi.spyOn(api, 'getAdminSession').mockRejectedValue(
      new ApiError(401, { code: 'ADMIN_SESSION_REQUIRED', title: '需要管理员登录' }),
    )
    const loginAdmin = vi.spyOn(api, 'loginAdmin').mockResolvedValue(adminSession)

    renderAdminApp('/admin/login')

    await screen.findByRole('heading', { name: '管理员登录' })
    await user.type(screen.getByLabelText('用户名'), '  operator  ')
    await user.type(screen.getByLabelText('密码'), 'correct horse battery staple')
    await user.click(screen.getByRole('button', { name: '进入管理后台' }))

    expect(await screen.findByRole('heading', { name: '用户目录' })).toBeInTheDocument()
    expect(loginAdmin.mock.calls[0]?.[0]).toEqual({
      username: 'operator',
      password: 'correct horse battery staple',
    })
  })

  it('presents the server login error without leaving the login page', async () => {
    const user = userEvent.setup()
    vi.spyOn(api, 'getAdminSession').mockRejectedValue(
      new ApiError(401, { code: 'ADMIN_SESSION_REQUIRED', title: '需要管理员登录' }),
    )
    vi.spyOn(api, 'loginAdmin').mockRejectedValue(
      new ApiError(401, {
        code: 'ADMIN_LOGIN_FAILED',
        title: '登录失败',
        detail: '用户名或密码不正确。',
      }),
    )

    renderAdminApp('/admin/login')

    await screen.findByRole('heading', { name: '管理员登录' })
    await user.type(screen.getByLabelText('用户名'), 'operator')
    await user.type(screen.getByLabelText('密码'), 'wrong password')
    await user.click(screen.getByRole('button', { name: '进入管理后台' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('用户名或密码不正确。')
    expect(screen.getByRole('heading', { name: '管理员登录' })).toBeInTheDocument()
  })

  it('returns an expired protected session to login with an explanation', async () => {
    vi.spyOn(api, 'getAdminSession')
      .mockResolvedValueOnce(adminSession)
      .mockRejectedValueOnce(
        new ApiError(401, { code: 'ADMIN_SESSION_REQUIRED', title: '需要管理员登录' }),
      )

    renderAdminApp('/admin/users')
    expect(await screen.findByRole('heading', { name: '用户目录' })).toBeInTheDocument()

    await act(async () => {
      window.dispatchEvent(new Event('mistchess:admin-session-invalid'))
    })

    expect(await screen.findByText(
      '管理员会话已过期，请重新登录后继续。',
    )).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: '管理员登录' })).toBeInTheDocument()
  })

  it('clears protected data when the server-issued session expiry is reached', async () => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date('2030-01-01T00:00:00Z'))
    vi.spyOn(api, 'getAdminSession')
      .mockResolvedValueOnce({
        ...adminSession,
        expiresAt: '2030-01-01T00:00:01Z',
      })
      .mockRejectedValueOnce(
        new ApiError(401, { code: 'ADMIN_SESSION_REQUIRED', title: '需要管理员登录' }),
      )

    renderAdminApp('/admin/users')
    await act(async () => {
      await vi.advanceTimersByTimeAsync(0)
    })
    expect(screen.getByRole('heading', { name: '用户目录' })).toBeInTheDocument()

    await act(async () => {
      await vi.advanceTimersByTimeAsync(1_001)
      await vi.runAllTimersAsync()
    })

    expect(screen.getByText(
      '管理员会话已过期，请重新登录后继续。',
    )).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: '管理员登录' })).toBeInTheDocument()
  })
})
