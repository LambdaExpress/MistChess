import { useEffect, useSyncExternalStore } from 'react'
import { NavLink, Outlet } from 'react-router'
import { audioService } from '../features/audio/audioService'

export function AppShell() {
  const audioSettings = useSyncExternalStore(
    audioService.subscribe,
    audioService.getSettings,
    audioService.getSettings,
  )

  useEffect(() => {
    const unlock = () => void audioService.unlock()
    window.addEventListener('pointerdown', unlock)
    window.addEventListener('keydown', unlock)
    return () => {
      window.removeEventListener('pointerdown', unlock)
      window.removeEventListener('keydown', unlock)
    }
  }, [])

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
        <div className="header-actions">
          <NavLink to="/history" className="header-link">历史对局</NavLink>
          <div className="audio-settings" aria-label="音效设置">
            <button
              type="button"
              aria-pressed={audioSettings.enabled}
              onClick={() => audioService.setEnabled(!audioSettings.enabled)}
            >
              {audioSettings.enabled ? '音效开启' : '音效静音'}
            </button>
            <label>
              <span>音量</span>
              <input
                type="range"
                min="0"
                max="100"
                value={Math.round(audioSettings.volume * 100)}
                disabled={!audioSettings.enabled}
                aria-label="音效音量"
                onChange={(event) => audioService.setVolume(Number(event.target.value) / 100)}
              />
            </label>
          </div>
          <div className="header-rule">
            <span className="status-dot" aria-hidden="true" />
            fog-xiangqi-v1
          </div>
        </div>
      </header>
      <main className="app-main">
        <Outlet />
      </main>
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
