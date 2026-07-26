import { NavLink, Outlet } from 'react-router'

export function AppShell() {
  return (
    <div className="app-shell">
      <header className="site-header">
        <NavLink to="/" className="brand" aria-label="迷雾象棋首页">
          <span className="brand__mark" aria-hidden="true">雾</span>
          <span>
            <strong>迷雾象棋</strong>
            <small>MIST XIANGQI</small>
          </span>
        </NavLink>
        <div className="header-rule">
          <span className="status-dot" aria-hidden="true" />
          fog-xiangqi-v1
        </div>
      </header>
      <main className="app-main">
        <Outlet />
      </main>
      <footer className="site-footer">
        服务端权威裁定 · 双方独立视野 · 游客即刻开局
      </footer>
    </div>
  )
}

export function PageLoader({ label = '正在连接棋局服务…' }: { label?: string }) {
  return (
    <div className="center-state" role="status">
      <span className="spinner" aria-hidden="true" />
      <p>{label}</p>
    </div>
  )
}

export function ErrorPanel({
  title,
  detail,
  onRetry,
}: {
  title: string
  detail: string
  onRetry?: () => void
}) {
  return (
    <section className="error-panel" role="alert">
      <span className="error-panel__glyph" aria-hidden="true">!</span>
      <div>
        <h1>{title}</h1>
        <p>{detail}</p>
        {onRetry ? (
          <button type="button" className="button button--secondary" onClick={onRetry}>
            重新尝试
          </button>
        ) : null}
      </div>
    </section>
  )
}
