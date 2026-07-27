import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef, useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { ApiError, api, errorMessage } from '../api/client'
import { useGameHub, type RealtimeState } from '../api/hubs'
import { queryKeys } from '../api/queryKeys'
import {
  QUICK_MATCH_CLIENT_REQUEST_ID_KEY,
  createClientId,
  type DrawOffer,
  type GameResult,
  type GameView,
  type GuestSession,
  type MatchTicket,
  type PieceType,
  type Position,
  type Side,
} from '../api/types'
import { ErrorPanel, PageLoader } from '../components/AppShell'
import { GameBoard } from '../components/board/GameBoard'
import {
  replaceWithAuthoritativeGameView,
  replaceWithNewerGameView,
} from '../features/game/gameViewCache'
import { audioService, type SoundEvent } from '../features/audio/audioService'
import { interpolateClock } from '../features/game/clock'

const sideNames: Record<Side, string> = { red: '红方', black: '黑方' }
const pieceNames: Record<Side, Record<PieceType, string>> = {
  red: { general: '帅', advisor: '仕', elephant: '相', horse: '马', rook: '车', cannon: '炮', pawn: '兵' },
  black: { general: '将', advisor: '士', elephant: '象', horse: '马', rook: '车', cannon: '炮', pawn: '卒' },
}
const realtimeLabels: Record<RealtimeState, string> = {
  connecting: '实时连接中',
  connected: '实时同步',
  reconnecting: '正在恢复实时连接',
  disconnected: '实时连接中断',
}
const resultReasons: Record<GameResult['reason'], string> = {
  generalCaptured: '将帅被吃',
  noLegalMove: '无合法走法',
  resignation: '认输',
  timeout: '超时',
  agreedDraw: '双方同意和棋',
  repetition: '三次重复局面',
  noProgress: '一百二十回合无进展',
}

type ClockSnapshot = {
  version: number
  redMilliseconds: number
  blackMilliseconds: number
  receivedAt: number
  sideToMove: Side
  playing: boolean
}


function useInterpolatedClock(view: GameView | undefined) {
  const snapshotRef = useRef<ClockSnapshot | null>(null)
  const [monotonicNow, setMonotonicNow] = useState(() => performance.now())

  useEffect(() => {
    if (!view?.clock) {
      snapshotRef.current = null
      return
    }

    const receivedAt = performance.now()
    snapshotRef.current = {
      version: view.version,
      redMilliseconds: view.clock.redMilliseconds,
      blackMilliseconds: view.clock.blackMilliseconds,
      receivedAt,
      sideToMove: view.sideToMove,
      playing: view.status === 'playing',
    }
    setMonotonicNow(receivedAt)
  }, [view])

  useEffect(() => {
    if (!view?.clock || view.status !== 'playing') return
    const timer = window.setInterval(() => setMonotonicNow(performance.now()), 200)
    return () => window.clearInterval(timer)
  }, [view?.clock, view?.status])

  const snapshot = snapshotRef.current
  if (!snapshot) return view?.clock ?? null
  const remaining = interpolateClock(
    snapshot.redMilliseconds,
    snapshot.blackMilliseconds,
    snapshot.sideToMove,
    snapshot.playing,
    monotonicNow - snapshot.receivedAt,
  )
  return {
    ...remaining,
    serverTime: view?.clock?.serverTime ?? '',
  }
}

function formatClock(milliseconds: number): string {
  const safeMilliseconds = Math.max(0, milliseconds)
  if (safeMilliseconds < 10_000) {
    return (safeMilliseconds / 1_000).toFixed(1)
  }

  const totalSeconds = Math.ceil(safeMilliseconds / 1_000)
  const minutes = Math.floor(totalSeconds / 60).toString().padStart(2, '0')
  const seconds = (totalSeconds % 60).toString().padStart(2, '0')
  return `${minutes}:${seconds}`
}

export function GamePage() {
  const { gameId = '' } = useParams<{ gameId: string }>()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [drawOffer, setDrawOffer] = useState<DrawOffer | null>(null)
  const [opponentConnected, setOpponentConnected] = useState(true)
  const [confirmResign, setConfirmResign] = useState(false)
  const [commandError, setCommandError] = useState<string | null>(null)
  const commandLock = useRef(false)
  const [commandPending, setCommandPending] = useState(false)

  const previousView = useRef<GameView | null>(null)
  const lowThresholds = useRef(new Set<number>())
  const previousOwnRemaining = useRef<number | null>(null)
  const runCommand = (command: () => void) => {
    if (commandLock.current) return
    commandLock.current = true
    setCommandPending(true)
    setCommandError(null)
    command()
  }

  const releaseCommand = () => {
    commandLock.current = false
    setCommandPending(false)
  }

  const gameQuery = useQuery({
    queryKey: queryKeys.game(gameId),
    queryFn: async () => {
      const incoming = await api.getGame(gameId)
      const current = queryClient.getQueryData<GameView>(queryKeys.game(gameId))
      return replaceWithAuthoritativeGameView(current, incoming)
    },
    enabled: gameId.length > 0,
    retry: 1,
  })
  const view = gameQuery.data
  const { refetch: refetchGame } = gameQuery
  const interpolatedClock = useInterpolatedClock(view)

  useEffect(() => {
    setDrawOffer(view?.drawOffer ?? null)
  }, [view?.drawOffer])

  const storeView = (incoming: GameView) => {
    queryClient.setQueryData<GameView>(queryKeys.game(gameId), (current) =>
      replaceWithNewerGameView(current, incoming),
    )
  }

  const realtimeState = useGameHub({
    gameId,
    version: view?.version ?? 0,
    onView: storeView,
    onDrawOffer: setDrawOffer,
    onOpponentConnection: setOpponentConnected,
    onReconnect: () => {
      void refetchGame()
    },
  })

  useEffect(() => {
    const refreshAfterVisibilityChange = () => {
      if (document.visibilityState === 'visible') {
        void refetchGame()
      }
    }
    document.addEventListener('visibilitychange', refreshAfterVisibilityChange)
    return () => document.removeEventListener('visibilitychange', refreshAfterVisibilityChange)
  }, [refetchGame])

  useEffect(() => {
    if (!view) return
    const previous = previousView.current
    const startKey = `mistchess.audio.started.${view.gameId}`
    const endKey = `mistchess.audio.ended.${view.gameId}.${view.version}`

    if (!previous && view.status === 'playing' && !sessionStorage.getItem(startKey)) {
      sessionStorage.setItem(startKey, '1')
      audioService.emit(view.gameId, view.version, 'game-start')
    }

    if (view.status === 'finished' && view.result && !sessionStorage.getItem(endKey)) {
      sessionStorage.setItem(endKey, '1')
      const terminalEvent: SoundEvent = view.result.winner === null
        ? 'game-draw'
        : view.result.winner === view.perspective
          ? 'game-win'
          : 'game-loss'
      audioService.emit(view.gameId, view.version, terminalEvent)
    } else if (previous && view.version > previous.version) {
      const captureCount = view.captureSummary.redLost.length +
        view.captureSummary.blackLost.length
      const previousCaptureCount = previous.captureSummary.redLost.length +
        previous.captureSummary.blackLost.length
      if (captureCount > previousCaptureCount) {
        audioService.emit(view.gameId, view.version, 'capture')
      } else if (view.sideToMove !== previous.sideToMove) {
        const movingSide = view.sideToMove === 'red' ? 'black' : 'red'
        audioService.emit(
          view.gameId,
          view.version,
          movingSide === view.perspective ? 'move-self' : 'move-opponent',
        )
      }
    }

    previousView.current = view
  }, [view])

  useEffect(() => {
    if (!view || !interpolatedClock || view.status !== 'playing') return
    const remaining = view.perspective === 'red'
      ? interpolatedClock.redMilliseconds
      : interpolatedClock.blackMilliseconds
    const previous = previousOwnRemaining.current
    if (previous !== null) {
      for (const threshold of [10_000, 5_000]) {
        if (
          previous > threshold &&
          remaining <= threshold &&
          !lowThresholds.current.has(threshold)
        ) {
          lowThresholds.current.add(threshold)
          audioService.emit(
            view.gameId,
            view.version,
            'clock-low',
            threshold.toString(),
          )
        }
      }
    }
    previousOwnRemaining.current = remaining
  }, [interpolatedClock, view])

  useEffect(() => {
    const ratingChange = view?.ratingChange
    if (!ratingChange) return
    queryClient.setQueryData<GuestSession>(queryKeys.session, (session) => {
      if (!session || session.rating.rating === ratingChange.after) return session
      return {
        ...session,
        rating: {
          ...session.rating,
          rating: ratingChange.after,
          gamesPlayed: session.rating.gamesPlayed + 1,
        },
      }
    })
  }, [queryClient, view?.ratingChange])

  const rematch = useMutation<MatchTicket, unknown, void>({
    mutationFn: async () => {
      let clientRequestId = sessionStorage.getItem(QUICK_MATCH_CLIENT_REQUEST_ID_KEY)
      if (!clientRequestId) {
        clientRequestId = createClientId()
        sessionStorage.setItem(QUICK_MATCH_CLIENT_REQUEST_ID_KEY, clientRequestId)
      }
      try {
        return await api.createMatchTicket(clientRequestId)
      } catch (error) {
        if (error instanceof ApiError && error.code === 'ACTIVE_TICKET_EXISTS') {
          return api.getCurrentMatchTicket()
        }
        throw error
      }
    },
    onSuccess: (ticket) => {
      queryClient.setQueryData(queryKeys.currentTicket, ticket)
      sessionStorage.removeItem(QUICK_MATCH_CLIENT_REQUEST_ID_KEY)
      void navigate('/match')
    },
    onError: (error) => {
      if (
        error instanceof ApiError &&
        error.code === 'ACTIVE_GAME_EXISTS' &&
        typeof error.problem.gameId === 'string'
      ) {
        void navigate(`/game/${encodeURIComponent(error.problem.gameId)}`)
      }
    },
  })

  const move = useMutation({
    mutationFn: ({ from, to, version }: { from: Position; to: Position; version: number }) =>
      api.submitMove(gameId, {
        from,
        to,
        expectedVersion: version,
        clientMoveId: createClientId(),
      }),
    onSuccess: storeView,
    onError: (error) => {
      if (error instanceof ApiError && error.code === 'STALE_VERSION') {
        setCommandError('棋局已更新，正在同步最新局面。')
        void refetchGame()
      } else if (error instanceof ApiError && error.code === 'ILLEGAL_MOVE') {
        setCommandError('该走法无法执行，请根据最新候选落点重试。')
      } else {
        setCommandError(errorMessage(error))
      }
    },
    onSettled: releaseCommand,
  })

  const resign = useMutation({
    mutationFn: () => api.resignGame(gameId),
    onSuccess: (updatedView) => {
      storeView(updatedView)
      setConfirmResign(false)
    },
    onError: (error) => setCommandError(errorMessage(error)),
    onSettled: releaseCommand,
  })
  const offerDraw = useMutation({
    mutationFn: () => api.offerDraw(gameId),
    onSuccess: setDrawOffer,
    onError: (error) => setCommandError(errorMessage(error)),
    onSettled: releaseCommand,
  })
  const acceptDraw = useMutation({
    mutationFn: () => api.acceptDraw(gameId),
    onSuccess: (updatedView) => {
      storeView(updatedView)
      setDrawOffer(null)
    },
    onError: (error) => setCommandError(errorMessage(error)),
    onSettled: releaseCommand,
  })
  const rejectDraw = useMutation({
    mutationFn: () => api.rejectDraw(gameId),
    onSuccess: () => setDrawOffer(null),
    onError: (error) => setCommandError(errorMessage(error)),
    onSettled: releaseCommand,
  })

  if (!gameId) return <ErrorPanel title="棋局编号无效" detail="请从房间或匹配页面进入棋局。" />
  if (gameQuery.isPending) return <PageLoader label="正在获取你的迷雾棋局…" />
  if (gameQuery.isError || !view) {
    return (
      <ErrorPanel
        title="无法恢复棋局"
        detail={errorMessage(gameQuery.error)}
        onRetry={() => void refetchGame()}
      />
    )
  }

  const myTurn = view.status === 'playing' && view.sideToMove === view.perspective
  const incomingDrawOffer = drawOffer?.status === 'pending' && drawOffer.offeredBy !== view.perspective
  const isFinished = view.status === 'finished'
  const captured = view.captureSummary

  return (
    <div className="game-page">
      <header className="game-header">
        <div>
          <p className="page-kicker">LIVE GAME · {view.ruleVersion}</p>
          <h1>{isFinished ? '棋局结束' : myTurn ? '轮到你行棋' : `等待${sideNames[view.sideToMove]}行棋`}</h1>
        </div>
        <div className="game-connection" data-state={realtimeState}>
          <span aria-hidden="true" />
          <div><strong>{realtimeLabels[realtimeState]}</strong><small>局面版本 {view.version}</small></div>
        </div>
      </header>

      {!opponentConnected && !isFinished ? (
        <div className="notice-banner" role="status">对手暂时离线；棋局会在其使用原会话重连后继续。</div>
      ) : null}
      {commandError ? <div className="notice-banner notice-banner--error" role="alert">{commandError}</div> : null}

      <div className="game-layout">
        <section className="board-column">
          <GameBoard
            view={view}
            interactionLocked={commandPending}
            onMove={(from, to) =>
              runCommand(() => move.mutate({ from, to, version: view.version }))
            }
          />
          <div className="board-legend">
            <span><i className="legend-swatch legend-swatch--fog" />未知区域</span>
            <span><i className="legend-swatch legend-swatch--visible" />当前可见</span>
            <span><i className="legend-swatch legend-swatch--move" />候选落点</span>
          </div>
        </section>

        <aside className="game-sidebar" aria-label="棋局状态与操作">
          {interpolatedClock ? (
            <section className="clock-card">
              <div className={[
                'clock-row',
                view.sideToMove === 'black' ? 'clock-row--active' : '',
                view.sideToMove === 'black' && interpolatedClock.blackMilliseconds < 10_000
                  ? 'clock-row--low'
                  : '',
              ].filter(Boolean).join(' ')}>
                <span className="side-token side-token--black">将</span>
                <div>
                  <small>黑方</small>
                  <strong>{formatClock(interpolatedClock.blackMilliseconds)}</strong>
                  {view.sideToMove === 'black' && interpolatedClock.blackMilliseconds < 10_000
                    ? <em>时间不足</em>
                    : null}
                </div>
              </div>
              <div className={[
                'clock-row',
                view.sideToMove === 'red' ? 'clock-row--active' : '',
                view.sideToMove === 'red' && interpolatedClock.redMilliseconds < 10_000
                  ? 'clock-row--low'
                  : '',
              ].filter(Boolean).join(' ')}>
                <span className="side-token side-token--red">帅</span>
                <div>
                  <small>红方</small>
                  <strong>{formatClock(interpolatedClock.redMilliseconds)}</strong>
                  {view.sideToMove === 'red' && interpolatedClock.redMilliseconds < 10_000
                    ? <em>时间不足</em>
                    : null}
                </div>
              </div>
            </section>
          ) : (
            <section className="turn-card">
              <span className={`side-token side-token--${view.sideToMove}`} aria-hidden="true">
                {view.sideToMove === 'red' ? '帅' : '将'}
              </span>
              <div><small>当前行棋</small><strong>{sideNames[view.sideToMove]}</strong></div>
              <span className={myTurn ? 'turn-badge turn-badge--mine' : 'turn-badge'}>{myTurn ? '你的回合' : '对方回合'}</span>
            </section>
          )}

          {isFinished && view.result ? (
            <section className="result-card" role="status">
              <p className="page-kicker">FINAL RESULT</p>
              <h2>{view.result.winner ? `${sideNames[view.result.winner]}获胜` : '本局和棋'}</h2>
              <p>{resultReasons[view.result.reason]}</p>
              {view.ratingChange ? (
                <div className="rating-change" aria-label="本局匹配分变化">
                  <span>{view.ratingChange.before}</span>
                  <span aria-hidden="true">→</span>
                  <strong>{view.ratingChange.after}</strong>
                  <em>
                    {view.ratingChange.delta >= 0 ? '+' : ''}
                    {view.ratingChange.delta}
                  </em>
                </div>
              ) : null}
              <button
                type="button"
                className="button button--accent button--wide"
                disabled={rematch.isPending}
                aria-busy={rematch.isPending}
                onClick={() => rematch.mutate()}
              >
                {rematch.isPending ? '正在创建匹配…' : '重新匹配'}
              </button>
              {rematch.isError ? (
                <p className="inline-error" role="alert">{errorMessage(rematch.error)}</p>
              ) : null}
              <Link className="button button--secondary button--wide" to={`/history/${encodeURIComponent(gameId)}`}>
                查看完整回放
              </Link>
            </section>
          ) : (
            <section className="action-card">
              <h2>对局操作</h2>
              {incomingDrawOffer ? (
                <div className="draw-offer" role="alert">
                  <strong>对手提议和棋</strong>
                  <p>接受后棋局立即结束并记为和棋。</p>
                  <div>
                    <button type="button" className="button button--accent" disabled={commandPending} onClick={() => runCommand(() => acceptDraw.mutate())}>接受</button>
                    <button type="button" className="button button--secondary" disabled={commandPending} onClick={() => runCommand(() => rejectDraw.mutate())}>拒绝</button>
                  </div>
                </div>
              ) : drawOffer?.status === 'pending' ? (
                <p className="muted-copy">已发出和棋提议，等待对手回应。</p>
              ) : (
                <button type="button" className="button button--secondary button--wide" disabled={commandPending} onClick={() => runCommand(() => offerDraw.mutate())}>
                  {offerDraw.isPending ? '正在发送…' : '提议和棋'}
                </button>
              )}

              {confirmResign ? (
                <div className="confirm-action" role="alertdialog" aria-label="确认认输">
                  <p>认输后不能撤销，确定结束本局？</p>
                  <div>
                    <button type="button" className="button button--danger" disabled={commandPending} onClick={() => runCommand(() => resign.mutate())}>
                      {resign.isPending ? '正在提交…' : '确认认输'}
                    </button>
                    <button type="button" className="button button--secondary" disabled={commandPending} onClick={() => setConfirmResign(false)}>继续对局</button>
                  </div>
                </div>
              ) : (
                <button type="button" className="text-danger" disabled={commandPending} onClick={() => setConfirmResign(true)}>认输</button>
              )}
            </section>
          )}

          <section className="capture-card">
            <h2>已损失棋子</h2>
            <div><span>红方</span><p>{captured.redLost.length ? captured.redLost.map((piece) => pieceNames.red[piece]).join(' ') : '—'}</p></div>
            <div><span>黑方</span><p>{captured.blackLost.length ? captured.blackLost.map((piece) => pieceNames.black[piece]).join(' ') : '—'}</p></div>
          </section>

          <section className="fog-note">
            <strong>迷雾提示</strong>
            <p>棋盘只展示服务器返回的当前视野。敌子离开视野后不会保留旧位置。</p>
          </section>
        </aside>
      </div>
    </div>
  )
}
