import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef, useState, type FormEvent } from 'react'
import { Link, useParams } from 'react-router'
import { api, errorMessage } from '../../api/client'
import { queryKeys } from '../../api/queryKeys'
import {
  QUICK_MATCH_TIME_CONTROL_ID,
  RULE_VERSION,
  type AdminUser,
  type HistoricalGame,
} from '../../api/types'
import { ErrorPanel, PageLoader } from '../../components/AppShell'

const dateTimeFormatter = new Intl.DateTimeFormat('zh-CN', {
  dateStyle: 'medium',
  timeStyle: 'short',
})
const outcomeLabels: Record<HistoricalGame['red']['outcome'], string> = {
  win: '胜',
  loss: '负',
  draw: '和',
}
const reasonLabels: Record<string, string> = {
  generalCaptured: '将帅被吃',
  noLegalMove: '无合法走法',
  resignation: '认输',
  timeout: '超时',
  agreedDraw: '协议和棋',
  repetition: '重复局面和棋',
  noProgress: '无进展和棋',
  administrativeForfeit: '管理员封禁判负',
}

type ModerationMode = 'ban' | 'unban'

function ModerationDialog({
  mode,
  user,
  pending,
  error,
  onClose,
  onConfirm,
}: {
  mode: ModerationMode
  user: AdminUser
  pending: boolean
  error: unknown
  onClose: () => void
  onConfirm: (reason?: string) => void
}) {
  const [reason, setReason] = useState('')
  const banReasonControl = useRef<HTMLTextAreaElement>(null)
  const confirmControl = useRef<HTMLButtonElement>(null)
  const normalizedReason = reason.trim()
  const validReason = normalizedReason.length >= 1 && normalizedReason.length <= 200

  useEffect(() => {
    if (mode === 'ban') banReasonControl.current?.focus()
    else confirmControl.current?.focus()
  }, [mode])

  useEffect(() => {
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && !pending) onClose()
    }
    window.addEventListener('keydown', closeOnEscape)
    return () => window.removeEventListener('keydown', closeOnEscape)
  }, [onClose, pending])

  const submit = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (pending || (mode === 'ban' && !validReason)) return
    onConfirm(mode === 'ban' ? normalizedReason : undefined)
  }

  return (
    <div className="admin-dialog-backdrop">
      <section
        className="admin-dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby="moderation-dialog-title"
      >
        <form onSubmit={submit}>
          <p className="admin-kicker">MODERATION ACTION</p>
          <h2 id="moderation-dialog-title">
            {mode === 'ban' ? `确认封禁 ${user.displayName}` : `确认解封 ${user.displayName}`}
          </h2>
          <p className="admin-dialog__identity">
            目标用户 <code>{user.playerId}</code>
          </p>

          {mode === 'ban' ? (
            <>
              <p>
                封禁会立即取消匹配与未开始的房间；进行中的棋局将以管理员封禁判负结束。
              </p>
              <label className="admin-field" htmlFor="admin-ban-reason">
                <span>封禁原因（1–200 字）</span>
                <textarea
                  ref={banReasonControl}
                  id="admin-ban-reason"
                  value={reason}
                  maxLength={200}
                  disabled={pending}
                  aria-describedby="admin-ban-privacy admin-ban-count"
                  required
                  onChange={(event) => setReason(event.target.value)}
                />
              </label>
              <p id="admin-ban-count" className="admin-character-count">
                去除首尾空格后 {normalizedReason.length} / 200 字
              </p>
              <div id="admin-ban-privacy" className="admin-privacy-warning">
                此原因会显示给被封用户。不得填写密码、联系方式或其他无关敏感信息。
              </div>
            </>
          ) : (
            <>
              <p>
                解封仅恢复该游客身份后续访问，不会恢复已取消的匹配、退出的房间或已结束的棋局。
              </p>
            </>
          )}

          {error ? <div className="admin-notice admin-notice--danger" role="alert">{errorMessage(error)}</div> : null}

          <div className="admin-dialog__actions">
            <button
              type="button"
              className="admin-button admin-button--quiet"
              disabled={pending}
              onClick={onClose}
            >
              取消
            </button>
            <button
              ref={confirmControl}
              type="submit"
              className={mode === 'ban'
                ? 'admin-button admin-button--danger'
                : 'admin-button admin-button--primary'}
              disabled={pending || (mode === 'ban' && !validReason)}
            >
              {pending ? '正在提交…' : mode === 'ban' ? '确认封禁' : '确认解封'}
            </button>
          </div>
        </form>
      </section>
    </div>
  )
}

export function AdminUserDetailPage() {
  const { playerId = '' } = useParams<{ playerId: string }>()
  const queryClient = useQueryClient()
  const [moderationMode, setModerationMode] = useState<ModerationMode | null>(null)
  const [announcement, setAnnouncement] = useState('')
  const detailQuery = useQuery({
    queryKey: queryKeys.adminUser(playerId),
    queryFn: () => api.getAdminUser(playerId),
    enabled: playerId.length > 0,
    retry: false,
    refetchInterval: 15_000,
  })
  const historyQuery = useInfiniteQuery({
    queryKey: queryKeys.adminUserGames(playerId),
    queryFn: ({ pageParam }) => api.getAdminUserGames(playerId, {
      cursor: pageParam,
      limit: 20,
    }),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (page) => page.nextCursor ?? undefined,
    enabled: playerId.length > 0,
    retry: false,
  })
  const moderation = useMutation({
    mutationFn: (action: { mode: ModerationMode; reason?: string }) =>
      action.mode === 'ban'
        ? api.banAdminUser(playerId, action.reason ?? '')
        : api.unbanAdminUser(playerId),
    onSuccess: async (_, action) => {
      setModerationMode(null)
      setAnnouncement(action.mode === 'ban' ? '用户已封禁。' : '用户已解除封禁。')
      await queryClient.invalidateQueries({
        queryKey: queryKeys.adminUsersRoot,
        refetchType: 'all',
      })
    },
  })

  if (!playerId) {
    return <ErrorPanel title="用户编号无效" detail="请从管理员用户列表进入用户详情。" />
  }

  if (detailQuery.isPending) {
    return <PageLoader label="正在读取用户详情…" />
  }

  if (detailQuery.isError) {
    return (
      <ErrorPanel
        title="暂时无法读取用户详情"
        detail={errorMessage(detailQuery.error)}
        onRetry={() => void detailQuery.refetch()}
      />
    )
  }

  const { user, ratings, observedAt } = detailQuery.data
  const history = historyQuery.data?.pages.flatMap((page) => page.games) ?? []
  const primaryRating = ratings.find((rating) =>
    rating.ruleVersion === RULE_VERSION && rating.timeControl === QUICK_MATCH_TIME_CONTROL_ID)

  return (
    <div className="admin-page admin-user-detail">
      <div className="admin-breadcrumbs" aria-label="面包屑">
        <Link to="/admin/users">用户管理</Link>
        <span aria-hidden="true">/</span>
        <span>{user.displayName}</span>
      </div>

      <header className="admin-page-header admin-user-heading">
        <div>
          <p className="admin-kicker">PLAYER RECORD</p>
          <h1>{user.displayName}</h1>
          <code>{user.playerId}</code>
        </div>
        <div className="admin-user-heading__status">
          <span className={`admin-status admin-status--${user.banned ? 'banned' : user.online ? 'online' : 'offline'}`}>
            {user.banned ? '已封禁' : user.online ? '当前在线' : '当前离线'}
          </span>
          <small>观测于 {dateTimeFormatter.format(new Date(observedAt))}</small>
        </div>
      </header>

      <p className="sr-only" aria-live="polite">{announcement}</p>

      <section className="admin-summary-grid" aria-label="主要评分摘要">
        <article className="admin-summary admin-summary--rating">
          <small>当前快速匹配评分</small>
          <strong>{primaryRating?.rating ?? user.rating}</strong>
          <span>{RULE_VERSION} · {QUICK_MATCH_TIME_CONTROL_ID}</span>
        </article>
        <article className="admin-summary">
          <small>计分局数</small>
          <strong>{primaryRating?.gamesPlayed ?? user.gamesPlayed}</strong>
          <span>胜 {user.wins} · 和 {user.draws} · 负 {user.losses}</span>
        </article>
        <article className="admin-summary">
          <small>胜率</small>
          <strong>{user.winRate === null ? '—' : `${user.winRate.toFixed(1)}%`}</strong>
          <span>按胜局 / 计分局数计算</span>
        </article>
      </section>

      <div className="admin-detail-columns">
        <section className="admin-panel" aria-labelledby="account-details-title">
          <div className="admin-section-heading">
            <div>
              <p className="admin-kicker">ACCOUNT</p>
              <h2 id="account-details-title">身份与状态</h2>
            </div>
            <button
              type="button"
              className={user.banned
                ? 'admin-button admin-button--quiet'
                : 'admin-button admin-button--danger'}
              onClick={() => {
                moderation.reset()
                setModerationMode(user.banned ? 'unban' : 'ban')
              }}
            >
              {user.banned ? '解除封禁' : '封禁用户'}
            </button>
          </div>
          <dl className="admin-definition-list">
            <div><dt>创建时间</dt><dd>{dateTimeFormatter.format(new Date(user.createdAt))}</dd></div>
            <div><dt>会话到期</dt><dd>{dateTimeFormatter.format(new Date(user.expiresAt))}</dd></div>
            <div><dt>最后活动</dt><dd>{dateTimeFormatter.format(new Date(user.lastSeenAt))}</dd></div>
            <div><dt>封禁时间</dt><dd>{user.bannedAt ? dateTimeFormatter.format(new Date(user.bannedAt)) : '—'}</dd></div>
            <div><dt>执行管理员</dt><dd>{user.bannedBy ?? '—'}</dd></div>
            <div className="admin-definition-list__wide"><dt>封禁原因</dt><dd>{user.banReason ?? '—'}</dd></div>
          </dl>
        </section>

        <section className="admin-panel" aria-labelledby="rating-tiers-title">
          <div className="admin-section-heading">
            <div>
              <p className="admin-kicker">RATING TIERS</p>
              <h2 id="rating-tiers-title">全部评分档</h2>
            </div>
          </div>
          {ratings.length ? (
            <div className="admin-rating-list">
              {ratings.map((rating) => {
                const primary = rating.ruleVersion === RULE_VERSION
                  && rating.timeControl === QUICK_MATCH_TIME_CONTROL_ID
                return (
                  <article key={`${rating.ruleVersion}:${rating.timeControl}`} data-primary={primary}>
                    <div>
                      <strong>{rating.rating}</strong>
                      {primary ? <span>当前主档</span> : null}
                    </div>
                    <p>{rating.ruleVersion} · {rating.timeControl}</p>
                    <small>
                      {rating.gamesPlayed} 局 · {rating.wins} 胜 {rating.draws} 和 {rating.losses} 负 ·
                      {' '}{rating.winRate === null ? '胜率 —' : `胜率 ${rating.winRate.toFixed(1)}%`}
                    </small>
                  </article>
                )
              })}
            </div>
          ) : (
            <div className="admin-compact-empty">尚无评分记录；主要评分按基础分 1500 显示。</div>
          )}
        </section>
      </div>

      <section className="admin-history" aria-labelledby="admin-history-title">
        <div className="admin-section-heading">
          <div>
            <p className="admin-kicker">COMPLETE HISTORY</p>
            <h2 id="admin-history-title">全部历史棋局</h2>
          </div>
          <span>{history.length} 条已载入</span>
        </div>

        {historyQuery.isPending ? (
          <div className="admin-inline-state" role="status">正在读取历史棋局…</div>
        ) : historyQuery.isError ? (
          <div className="admin-notice admin-notice--danger" role="alert">
            <p>{errorMessage(historyQuery.error)}</p>
            <button type="button" className="admin-button admin-button--quiet" onClick={() => void historyQuery.refetch()}>
              重新尝试
            </button>
          </div>
        ) : history.length ? (
          <div className="admin-history-list">
            {history.map((game) => {
              const playerOutcome = game.currentPlayerSide === 'red'
                ? game.red.outcome
                : game.black.outcome
              return (
                <article className="admin-history-row" key={game.gameId}>
                  <div className="admin-history-row__result">
                    <strong data-outcome={playerOutcome}>{outcomeLabels[playerOutcome]}</strong>
                    <span>{game.currentPlayerSide === 'red' ? '用户执红' : '用户执黑'} · {game.isRated ? '计分' : '非计分'}</span>
                  </div>
                  <div className="admin-history-row__players">
                    <p><span>红</span><strong>{game.red.displayName}</strong></p>
                    <p><span>黑</span><strong>{game.black.displayName}</strong></p>
                  </div>
                  <dl>
                    <div><dt>棋局 ID</dt><dd>{game.gameId}</dd></div>
                    <div><dt>结束时间</dt><dd>{dateTimeFormatter.format(new Date(game.finishedAt))}</dd></div>
                    <div><dt>终局原因</dt><dd>{reasonLabels[game.result.reason] ?? game.result.reason}</dd></div>
                    <div><dt>规则与计时</dt><dd>{game.ruleVersion} · {game.timeControl ?? '无计时'}{game.moveTimeLimitSeconds ? ` · 每步 ${game.moveTimeLimitSeconds} 秒` : ''}</dd></div>
                    <div><dt>总手数</dt><dd>{game.plyCount} 个半回合</dd></div>
                  </dl>
                  <Link className="admin-row-link" to={`/admin/games/${encodeURIComponent(game.gameId)}`}>
                    查看三视野回放<span aria-hidden="true"> ›</span>
                  </Link>
                </article>
              )
            })}
          </div>
        ) : (
          <div className="admin-empty admin-empty--compact">
            <h3>该用户尚无已结束棋局</h3>
            <p>计分与非计分棋局结束后都会出现在这里。</p>
          </div>
        )}

        {historyQuery.hasNextPage ? (
          <button
            type="button"
            className="admin-button admin-button--quiet admin-history__more"
            disabled={historyQuery.isFetchingNextPage}
            onClick={() => void historyQuery.fetchNextPage()}
          >
            {historyQuery.isFetchingNextPage ? '正在加载…' : '加载更多历史棋局'}
          </button>
        ) : null}
      </section>

      {moderationMode ? (
        <ModerationDialog
          mode={moderationMode}
          user={user}
          pending={moderation.isPending}
          error={moderation.error}
          onClose={() => setModerationMode(null)}
          onConfirm={(reason) => moderation.mutate({ mode: moderationMode, reason })}
        />
      ) : null}
    </div>
  )
}
