import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { Navigate, useLocation, useNavigate } from 'react-router'
import { api, errorMessage } from '../../api/client'
import { queryKeys } from '../../api/queryKeys'
import { PageLoader } from '../../components/AppShell'

export function AdminLoginPage() {
  const location = useLocation()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const sessionExpired = Boolean(
    (location.state as { adminSessionExpired?: boolean } | null)?.adminSessionExpired,
  )
  const sessionQuery = useQuery({
    queryKey: queryKeys.adminSession,
    queryFn: api.getAdminSession,
    retry: false,
    staleTime: 30_000,
    enabled: !sessionExpired,
  })
  const login = useMutation({
    mutationFn: api.loginAdmin,
    onSuccess: (session) => {
      queryClient.setQueryData(queryKeys.adminSession, session)
      void navigate('/admin/users', { replace: true })
    },
  })

  if (!sessionExpired && sessionQuery.isPending) {
    return <PageLoader label="正在检查管理员登录状态…" />
  }

  if (!sessionExpired && sessionQuery.isSuccess) {
    return <Navigate to="/admin/users" replace />
  }

  const submit = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    const normalizedUsername = username.trim()
    if (!normalizedUsername || !password || login.isPending) return
    login.mutate({ username: normalizedUsername, password })
  }

  return (
    <section className="admin-login" aria-labelledby="admin-login-title">
      <div className="admin-login__intro">
        <p className="admin-kicker">RESTRICTED ACCESS</p>
        <h1 id="admin-login-title">管理员登录</h1>
        <p>使用部署配置中的管理员凭据进入。管理员会话最多持续八小时，不会创建或替换游客身份。</p>
      </div>

      <form className="admin-login__form" onSubmit={submit}>
        {sessionExpired ? (
          <div className="admin-notice admin-notice--warning" role="status">
            管理员会话已过期，请重新登录后继续。
          </div>
        ) : null}
        {login.isError ? (
          <div className="admin-notice admin-notice--danger" role="alert">
            {errorMessage(login.error)}
          </div>
        ) : null}

        <label className="admin-field" htmlFor="admin-username">
          <span>用户名</span>
          <input
            id="admin-username"
            name="username"
            type="text"
            autoComplete="username"
            maxLength={64}
            value={username}
            disabled={login.isPending}
            required
            autoFocus
            onChange={(event) => setUsername(event.target.value)}
          />
        </label>
        <label className="admin-field" htmlFor="admin-password">
          <span>密码</span>
          <input
            id="admin-password"
            name="password"
            type="password"
            autoComplete="current-password"
            maxLength={1024}
            value={password}
            disabled={login.isPending}
            required
            onChange={(event) => setPassword(event.target.value)}
          />
        </label>
        <button
          type="submit"
          className="admin-button admin-button--primary admin-button--wide"
          disabled={login.isPending || !username.trim() || !password}
        >
          {login.isPending ? '正在验证…' : '进入管理后台'}
        </button>
        <p className="admin-login__security">凭据仅发送至当前站点，不会写入浏览器存储。</p>
      </form>
    </section>
  )
}
