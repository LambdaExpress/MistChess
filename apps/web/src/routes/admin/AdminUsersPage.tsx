import { useQuery } from '@tanstack/react-query'
import { useEffect, useState, type FormEvent } from 'react'
import { Link, useSearchParams } from 'react-router'
import { api, errorMessage } from '../../api/client'
import { queryKeys } from '../../api/queryKeys'
import type { AdminOnlineStatus, AdminUserStatus } from '../../api/types'
import { ErrorPanel, PageLoader } from '../../components/AppShell'

const dateTimeFormatter = new Intl.DateTimeFormat('zh-CN', {
  dateStyle: 'medium',
  timeStyle: 'short',
})

export function AdminUsersPage() {
  const [searchParams, setSearchParams] = useSearchParams()
  const query = searchParams.get('query')?.trim() ?? ''
  const statusParameter = searchParams.get('status')
  const onlineParameter = searchParams.get('online')
  const cursor = searchParams.get('cursor') ?? undefined
  const status: AdminUserStatus = statusParameter === 'active' || statusParameter === 'banned'
    ? statusParameter
    : 'all'
  const online: AdminOnlineStatus = onlineParameter === 'online' || onlineParameter === 'offline'
    ? onlineParameter
    : 'all'
  const [searchDraft, setSearchDraft] = useState(query)
  const [previousCursors, setPreviousCursors] = useState<Array<string | undefined>>([])
  const params = { query: query || undefined, status, online, cursor, limit: 20 }
  const usersQuery = useQuery({
    queryKey: queryKeys.adminUsers(params),
    queryFn: () => api.getAdminUsers(params),
    retry: false,
    refetchInterval: online === 'online' ? 15_000 : false,
  })

  useEffect(() => {
    setSearchDraft(query)
    setPreviousCursors([])
  }, [online, query, status])

  const updateFilters = (next: { query?: string; status?: AdminUserStatus }) => {
    setPreviousCursors([])
    setSearchParams((current) => {
      const updated = new URLSearchParams(current)
      updated.delete('cursor')
      if (next.query !== undefined) {
        if (next.query) updated.set('query', next.query)
        else updated.delete('query')
      }
      if (next.status !== undefined) {
        if (next.status === 'all') updated.delete('status')
        else updated.set('status', next.status)
      }
      return updated
    })
  }

  const submitSearch = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    updateFilters({ query: searchDraft.trim() })
  }

  if (usersQuery.isPending) {
    return <PageLoader label="正在读取管理员用户目录…" />
  }

  if (usersQuery.isError) {
    return (
      <ErrorPanel
        title="暂时无法读取用户目录"
        detail={errorMessage(usersQuery.error)}
        onRetry={() => void usersQuery.refetch()}
      />
    )
  }

  const page = usersQuery.data
  const filtered = Boolean(query || status !== 'all' || online !== 'all')
  const showPrevious = previousCursors.length > 0

  return (
    <div className="admin-page admin-users-page">
      <header className="admin-page-header">
        <div>
          <p className="admin-kicker">PLAYER DIRECTORY</p>
          <h1>{online === 'online' ? '当前在线用户' : '用户管理'}</h1>
          <p>
            {online === 'online'
              ? '在线状态依据服务器统一观测时间计算，本页每 15 秒自动刷新。'
              : '搜索游客显示名或完整用户 ID，并查看内部评分与封禁状态。'}
          </p>
        </div>
        <div className="admin-observed" aria-live="polite">
          <small>服务器观测时间</small>
          <strong>{dateTimeFormatter.format(new Date(page.observedAt))}</strong>
          {usersQuery.isFetching ? <span>正在刷新…</span> : null}
        </div>
      </header>

      <section className="admin-toolbar" aria-label="用户筛选">
        <form className="admin-search" role="search" onSubmit={submitSearch}>
          <label className="admin-field" htmlFor="admin-user-search">
            <span>名称或用户 ID</span>
            <input
              id="admin-user-search"
              type="search"
              value={searchDraft}
              placeholder="输入游客名称或完整 UUID"
              autoComplete="off"
              onChange={(event) => setSearchDraft(event.target.value)}
            />
          </label>
          <button type="submit" className="admin-button admin-button--primary">搜索</button>
        </form>
        <label className="admin-field admin-filter" htmlFor="admin-ban-status">
          <span>封禁状态</span>
          <select
            id="admin-ban-status"
            value={status}
            onChange={(event) => updateFilters({ status: event.target.value as AdminUserStatus })}
          >
            <option value="all">全部状态</option>
            <option value="active">正常</option>
            <option value="banned">已封禁</option>
          </select>
        </label>
      </section>

      {page.items.length ? (
        <section className="admin-table-panel" aria-label="用户结果">
          <div className="admin-table-scroll">
            <table className="admin-table">
              <caption className="sr-only">管理员用户查询结果</caption>
              <thead>
                <tr>
                  <th scope="col">用户</th>
                  <th scope="col">状态</th>
                  <th scope="col">主要评分</th>
                  <th scope="col">胜 / 和 / 负</th>
                  <th scope="col">胜率</th>
                  <th scope="col">最后活动</th>
                  <th scope="col"><span className="sr-only">操作</span></th>
                </tr>
              </thead>
              <tbody>
                {page.items.map((user) => (
                  <tr key={user.playerId}>
                    <td data-label="用户">
                      <strong>{user.displayName}</strong>
                      <code>{user.playerId}</code>
                    </td>
                    <td data-label="状态">
                      <span className={`admin-status admin-status--${user.banned ? 'banned' : user.online ? 'online' : 'offline'}`}>
                        {user.banned ? '已封禁' : user.online ? '在线' : '离线'}
                      </span>
                    </td>
                    <td data-label="主要评分">
                      <strong className="admin-rating">{user.rating}</strong>
                      <small>{user.gamesPlayed} 局</small>
                    </td>
                    <td data-label="胜 / 和 / 负">{user.wins} / {user.draws} / {user.losses}</td>
                    <td data-label="胜率">{user.winRate === null ? '—' : `${user.winRate.toFixed(1)}%`}</td>
                    <td data-label="最后活动">{dateTimeFormatter.format(new Date(user.lastSeenAt))}</td>
                    <td data-label="操作">
                      <Link
                        className="admin-row-link"
                        to={`/admin/users/${encodeURIComponent(user.playerId)}`}
                      >
                        查看详情<span aria-hidden="true"> ›</span>
                      </Link>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
      ) : (
        <section className="admin-empty">
          <span aria-hidden="true">档</span>
          <h2>没有符合条件的用户</h2>
          <p>{filtered ? '尝试缩短搜索内容或放宽封禁状态筛选。' : '用户建立游客会话后会出现在这里。'}</p>
          {filtered ? (
            <button
              type="button"
              className="admin-button admin-button--quiet"
              onClick={() => {
                setPreviousCursors([])
                setSearchParams(online === 'online' ? { online: 'online' } : {})
              }}
            >
              清除筛选
            </button>
          ) : null}
        </section>
      )}

      <nav className="admin-pagination" aria-label="用户列表分页">
        <button
          type="button"
          className="admin-button admin-button--quiet"
          disabled={!showPrevious || usersQuery.isFetching}
          onClick={() => {
            const previous = previousCursors.at(-1)
            setPreviousCursors((current) => current.slice(0, -1))
            setSearchParams((current) => {
              const updated = new URLSearchParams(current)
              if (previous) updated.set('cursor', previous)
              else updated.delete('cursor')
              return updated
            })
          }}
        >
          上一页
        </button>
        <span>第 {previousCursors.length + 1} 页</span>
        <button
          type="button"
          className="admin-button admin-button--quiet"
          disabled={!page.nextCursor || usersQuery.isFetching}
          onClick={() => {
            if (!page.nextCursor) return
            setPreviousCursors((current) => [...current, cursor])
            setSearchParams((current) => {
              const updated = new URLSearchParams(current)
              updated.set('cursor', page.nextCursor ?? '')
              return updated
            })
          }}
        >
          下一页
        </button>
      </nav>
    </div>
  )
}
