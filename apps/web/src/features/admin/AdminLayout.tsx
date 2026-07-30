import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useCallback, useEffect, useRef } from 'react'
import { Link, Outlet, useLocation, useNavigate } from 'react-router'
import { api } from '../../api/client'
import { queryKeys } from '../../api/queryKeys'
import '../../admin.css'

export function AdminLayout() {
  const location = useLocation()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const loginPage = location.pathname === '/admin/login'
  const protectedPage = location.pathname.startsWith('/admin/users')
    || location.pathname.startsWith('/admin/games')
  const onlineUsersPage = location.pathname === '/admin/users'
    && new URLSearchParams(location.search).get('online') === 'online'
  const adminChannelRef = useRef<BroadcastChannel | null>(null)
  const sessionQuery = useQuery({
    queryKey: queryKeys.adminSession,
    queryFn: api.getAdminSession,
    enabled: !loginPage,
    retry: false,
    staleTime: 30_000,
  })
  const clearAdminCache = useCallback(async () => {
    await queryClient.cancelQueries({ queryKey: queryKeys.adminRoot })
    queryClient.removeQueries({ queryKey: queryKeys.adminRoot })
  }, [queryClient])
  const expireSession = useCallback(() => {
    void clearAdminCache().then(() => {
      void navigate('/admin/login', {
        replace: true,
        state: { adminSessionExpired: true },
      })
    })
  }, [clearAdminCache, navigate])
  const logout = useMutation({
    mutationFn: api.logoutAdmin,
    onSuccess: async () => {
      await clearAdminCache()
      adminChannelRef.current?.postMessage('logout')
      void navigate('/admin/login', { replace: true })
    },
  })

  useEffect(() => {
    if (!protectedPage) return

    const receiveAdminMessage = (event: MessageEvent<unknown>) => {
      if (event.data === 'logout') expireSession()
    }
    const channel = typeof BroadcastChannel === 'function'
      ? new BroadcastChannel('mistchess-admin-session')
      : null
    adminChannelRef.current = channel
    channel?.addEventListener('message', receiveAdminMessage)
    window.addEventListener('mistchess:admin-session-invalid', expireSession)
    return () => {
      window.removeEventListener('mistchess:admin-session-invalid', expireSession)
      channel?.removeEventListener('message', receiveAdminMessage)
      channel?.close()
      if (adminChannelRef.current === channel) adminChannelRef.current = null
    }
  }, [expireSession, protectedPage])

  useEffect(() => {
    if (!protectedPage || !sessionQuery.data) return
    const expiresAt = Date.parse(sessionQuery.data.expiresAt)
    if (!Number.isFinite(expiresAt)) return
    let timeout: number | undefined
    const scheduleExpiry = () => {
      const remainingMilliseconds = expiresAt - Date.now()
      if (remainingMilliseconds <= 0) {
        expireSession()
        return
      }
      timeout = window.setTimeout(
        scheduleExpiry,
        Math.min(remainingMilliseconds, 2_147_483_647),
      )
    }
    scheduleExpiry()
    return () => {
      if (timeout !== undefined) window.clearTimeout(timeout)
    }
  }, [expireSession, protectedPage, sessionQuery.data])

  return (
    <div className={`admin-shell${loginPage ? ' admin-shell--login' : ''}`}>
      <header className="admin-header">
        <Link to="/admin" className="admin-brand" aria-label="迷雾象棋管理后台首页">
          <span className="admin-brand__seal" aria-hidden="true">雾</span>
          <span>
            <strong>迷雾象棋</strong>
            <small>ADMINISTRATION</small>
          </span>
        </Link>

        {!loginPage ? (
          <div className="admin-header__session">
            <span>
              <small>当前管理员</small>
              <strong>{sessionQuery.data?.username ?? '正在验证…'}</strong>
            </span>
            <div className="admin-header__logout">
              <button
                type="button"
                className="admin-button admin-button--quiet"
                disabled={logout.isPending}
                onClick={() => logout.mutate()}
              >
                {logout.isPending ? '正在退出…' : '退出登录'}
              </button>
              {logout.isError ? <small role="alert">退出失败，请稍后重试。</small> : null}
            </div>
          </div>
        ) : null}
      </header>

      {!loginPage ? (
        <nav className="admin-nav" aria-label="管理员导航">
          <Link
            to="/admin/users"
            aria-current={!onlineUsersPage && location.pathname.startsWith('/admin/users')
              ? 'page'
              : undefined}
            className={!onlineUsersPage && location.pathname.startsWith('/admin/users')
              ? 'admin-nav__link admin-nav__link--active'
              : 'admin-nav__link'}
          >
            全部用户
          </Link>
          <Link
            to="/admin/users?online=online"
            aria-current={onlineUsersPage ? 'page' : undefined}
            className={onlineUsersPage
              ? 'admin-nav__link admin-nav__link--active'
              : 'admin-nav__link'}
          >
            当前在线
          </Link>
        </nav>
      ) : null}

      <main className="admin-main">
        <Outlet />
      </main>

      <footer className="admin-footer">
        管理操作会影响正在进行的匹配与棋局，请在确认目标身份后执行。
      </footer>
    </div>
  )
}
