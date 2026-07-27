import { useInfiniteQuery, useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { Link } from 'react-router'
import { api, errorMessage } from '../api/client'
import { queryKeys } from '../api/queryKeys'
import type { GameResult, GuestSession, HistoricalGame } from '../api/types'
import { ErrorPanel, PageLoader } from '../components/AppShell'

const outcomeLabel: Record<HistoricalGame['red']['outcome'], string> = {
  win: '胜',
  loss: '负',
  draw: '和',
}
const resultReasonLabel: Record<GameResult['reason'], string> = {
  generalCaptured: '将帅被吃',
  noLegalMove: '无合法走法',
  resignation: '认输',
  timeout: '超时',
  agreedDraw: '协议和棋',
  repetition: '重复局面和棋',
  noProgress: '无进展和棋',
}

export function HistoryPage() {
  const [ruleVersion, setRuleVersion] = useState('')
  const [timeControl, setTimeControl] = useState('')
  const [result, setResult] = useState('')
  const sessionQuery = useQuery<GuestSession>({
    queryKey: queryKeys.session,
    queryFn: api.startGuestSession,
    staleTime: 60_000,
  })
  const optionsQuery = useQuery({
    queryKey: queryKeys.gameOptions,
    queryFn: api.getGameOptions,
    staleTime: Number.POSITIVE_INFINITY,
  })
  const sessionId = sessionQuery.data?.playerId ?? ''
  const historyQuery = useInfiniteQuery({
    queryKey: queryKeys.history(sessionId, ruleVersion, timeControl, result),
    queryFn: ({ pageParam }) => api.getGameHistory({
      cursor: pageParam,
      limit: 20,
      ruleVersion: ruleVersion || undefined,
      timeControl: timeControl || undefined,
      result: result || undefined,
    }),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (page) => page.nextCursor ?? undefined,
    enabled: sessionId.length > 0,
  })

  if (sessionQuery.isPending || historyQuery.isPending) {
    return <PageLoader label="正在读取你的历史棋局…" />
  }
  if (sessionQuery.isError || historyQuery.isError) {
    return (
      <ErrorPanel
        title="暂时无法读取历史棋局"
        detail={errorMessage(sessionQuery.error ?? historyQuery.error)}
        onRetry={() => void historyQuery.refetch()}
      />
    )
  }

  const games = historyQuery.data.pages.flatMap((page) => page.games)
  const timeControlLabel = new Map(
    optionsQuery.data?.roomTimeControls.map((option) => [option.id, option.label]) ?? [],
  )

  return (
    <div className="history-page">
      <header className="history-header">
        <div>
          <p className="page-kicker">PRIVATE HISTORY</p>
          <h1>我的历史对局</h1>
          <p>这里只显示当前游客身份亲自参加且已经结束的棋局。</p>
        </div>
        <div className="history-filters" aria-label="历史筛选">
          <label>
            <span>规则版本</span>
            <select value={ruleVersion} onChange={(event) => setRuleVersion(event.target.value)}>
              <option value="">全部</option>
              <option value={optionsQuery.data?.ruleVersion ?? 'fog-xiangqi-v1'}>
                {optionsQuery.data?.ruleVersion ?? 'fog-xiangqi-v1'}
              </option>
            </select>
          </label>
          <label>
            <span>计时模式</span>
            <select value={timeControl} onChange={(event) => setTimeControl(event.target.value)}>
              <option value="">全部</option>
              {optionsQuery.data?.roomTimeControls.map((option) => (
                <option key={option.id} value={option.id}>{option.label}</option>
              ))}
              <option value="untimed">无计时</option>
            </select>
          </label>
          <label>
            <span>我的结果</span>
            <select value={result} onChange={(event) => setResult(event.target.value)}>
              <option value="">全部</option>
              <option value="win">胜</option>
              <option value="loss">负</option>
              <option value="draw">和</option>
            </select>
          </label>
        </div>
      </header>

      {games.length ? (
        <section className="history-list" aria-label="已结束棋局">
          {games.map((game) => (
            <Link
              className="history-row"
              key={game.gameId}
              to={`/history/${encodeURIComponent(game.gameId)}`}
            >
              <div className="history-players">
                <div>
                  <span className="side-token side-token--red" aria-hidden="true">帅</span>
                  <p>
                    <strong>{game.red.displayName}</strong>
                    {game.currentPlayerSide === 'red' ? <small>我</small> : null}
                  </p>
                  <em data-outcome={game.red.outcome}>{outcomeLabel[game.red.outcome]}</em>
                </div>
                <div>
                  <span className="side-token side-token--black" aria-hidden="true">将</span>
                  <p>
                    <strong>{game.black.displayName}</strong>
                    {game.currentPlayerSide === 'black' ? <small>我</small> : null}
                  </p>
                  <em data-outcome={game.black.outcome}>{outcomeLabel[game.black.outcome]}</em>
                </div>
              </div>
              <dl>
                <div>
                  <dt>结束时间</dt>
                  <dd>{new Date(game.finishedAt).toLocaleString('zh-CN')}</dd>
                </div>
                <div>
                  <dt>计时</dt>
                  <dd>{game.timeControl ? timeControlLabel.get(game.timeControl) ?? game.timeControl : '无计时'}</dd>
                </div>
                <div>
                  <dt>结束原因</dt>
                  <dd>{resultReasonLabel[game.result.reason]}</dd>
                </div>
                <div>
                  <dt>总手数</dt>
                  <dd>{game.plyCount} 个半回合</dd>
                </div>
              </dl>
              <span className="history-row__action">查看回放 ›</span>
            </Link>
          ))}
        </section>
      ) : (
        <section className="empty-history">
          <h2>暂无符合条件的历史棋局</h2>
          <p>完成快速匹配或私人房间对局后，记录会出现在这里。</p>
          <Link to="/" className="button button--accent">开始一局</Link>
        </section>
      )}

      {historyQuery.hasNextPage ? (
        <button
          type="button"
          className="button button--secondary history-load-more"
          disabled={historyQuery.isFetchingNextPage}
          onClick={() => void historyQuery.fetchNextPage()}
        >
          {historyQuery.isFetchingNextPage ? '正在加载…' : '加载更多'}
        </button>
      ) : null}
    </div>
  )
}