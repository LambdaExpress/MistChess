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
  type TakebackRequestView,
} from '../api/types'
import { ErrorPanel, PageLoader } from '../components/AppShell'
import { GameBoard } from '../components/board/GameBoard'
import { audioService, type SoundEvent } from '../features/audio/audioService'
import { GameNegotiationOverlay } from '../features/game/GameNegotiationOverlay'
import { PlayerClockBar } from '../features/game/PlayerClockBar'
import { interpolateClock } from '../features/game/clock'
import {
  mergeDrawOfferChange,
  mergeGameView,
  mergeTakebackRequestChange,
} from '../features/game/gameViewCache'

const sideNames: Record<Side, string> = { red: '红方', black: '黑方' }
const pieceNames: Record<Side, Record<PieceType, string>> = {
  red: { general: '帅', advisor: '仕', elephant: '相', horse: '马', rook: '车', cannon: '炮', pawn: '兵' },
  black: { general: '将', advisor: '士', elephant: '象', horse: '马', rook: '车', cannon: '炮', pawn: '卒' },
}
const realtimeLabels: Record<RealtimeState, string> = {
  connecting: '正在连接实时棋局',
  connected: '实时棋局已连接',
  reconnecting: '正在恢复实时连接，操作已暂停',
  disconnected: '实时连接已中断，操作已暂停',
}
const resultReasons: Record<GameResult['reason'], string> = {
  generalCaptured: '将帅被吃',
  noLegalMove: '无合法走法',
  resignation: '认输',
  timeout: '超时',
  agreedDraw: '双方同意和棋',
  repetition: '三次重复局面',
  noProgress: '一百二十回合无进展',
  administrativeForfeit: '管理员判负',
}

type ClockSnapshot = {
  version: number
  redMilliseconds: number
  blackMilliseconds: number
  turnMilliseconds: number | null
  receivedAt: number
  sideToMove: Side
  playing: boolean
}

function useInterpolatedClock(view: GameView | undefined) {
  const clock = view?.clock
  const version = view?.version
  const sideToMove = view?.sideToMove
  const status = view?.status
  const snapshotRef = useRef<ClockSnapshot | null>(null)
  const [monotonicNow, setMonotonicNow] = useState(() => performance.now())

  useEffect(() => {
    if (!clock || version === undefined || !sideToMove || !status) {
      snapshotRef.current = null
      return
    }

    const receivedAt = performance.now()
    snapshotRef.current = {
      version,
      redMilliseconds: clock.redMilliseconds,
      blackMilliseconds: clock.blackMilliseconds,
      turnMilliseconds: clock.turnMilliseconds ?? null,
      receivedAt,
      sideToMove,
      playing: status === 'playing',
    }
    setMonotonicNow(receivedAt)
  }, [clock, sideToMove, status, version])

  useEffect(() => {
    if (!clock || status !== 'playing') return
    const timer = window.setInterval(() => setMonotonicNow(performance.now()), 200)
    return () => window.clearInterval(timer)
  }, [clock, status])

  const snapshot = snapshotRef.current
  if (!snapshot) {
    return clock
      ? { ...clock, turnMilliseconds: clock.turnMilliseconds ?? null }
      : null
  }
  const remaining = interpolateClock(
    snapshot.redMilliseconds,
    snapshot.blackMilliseconds,
    snapshot.sideToMove,
    snapshot.playing,
    monotonicNow - snapshot.receivedAt,
  )
  const elapsedMilliseconds = monotonicNow - snapshot.receivedAt
  return {
    ...remaining,
    serverTime: clock?.serverTime ?? '',
    turnMilliseconds: snapshot.turnMilliseconds === null || !snapshot.playing
      ? snapshot.turnMilliseconds
      : Math.max(0, snapshot.turnMilliseconds - elapsedMilliseconds),
  }
}

function terminalSound(view: GameView): SoundEvent | null {
  if (view.status !== 'finished' || !view.result) return null
  if (view.result.winner === null) return 'game-draw'
  return view.result.winner === view.perspective ? 'game-win' : 'game-loss'
}

function liveSoundEvents(view: GameView): readonly SoundEvent[] {
  const events: SoundEvent[] = []
  const action = view.lastAction?.version === view.version ? view.lastAction : null
  const terminal = terminalSound(view)

  if (action?.kind === 'capture') events.push('capture')
  if (terminal) {
    events.push(terminal)
  } else if (action?.kind === 'move') {
    events.push(action.actor === view.perspective ? 'move-self' : 'move-opponent')
  }
  return events
}

export function GamePage() {
  const { gameId = '' } = useParams<{ gameId: string }>()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [opponentConnected, setOpponentConnected] = useState(true)
  const [confirmResign, setConfirmResign] = useState(false)
  const [commandError, setCommandError] = useState<string | null>(null)
  const [negotiationError, setNegotiationError] = useState<string | null>(null)
  const commandLock = useRef(false)
  const [commandPending, setCommandPending] = useState(false)
  const audioBaselineVersion = useRef<number | null>(null)
  const lowThresholds = useRef(new Set<number>())
  const previousOwnRemaining = useRef<number | null>(null)

  const gameQuery = useQuery({
    queryKey: queryKeys.game(gameId),
    queryFn: async () => {
      const incoming = await api.getGame(gameId)
      audioBaselineVersion.current = Math.max(audioBaselineVersion.current ?? -1, incoming.version)
      const current = queryClient.getQueryData<GameView>(queryKeys.game(gameId))
      return mergeGameView(current, incoming)
    },
    enabled: gameId.length > 0,
    retry: 1,
  })
  const view = gameQuery.data
  const { refetch: refetchGame } = gameQuery
  const interpolatedClock = useInterpolatedClock(view)

  const storeRecoveryView = (incoming: GameView) => {
    audioBaselineVersion.current = Math.max(audioBaselineVersion.current ?? -1, incoming.version)
    queryClient.setQueryData<GameView>(queryKeys.game(gameId), (current) =>
      mergeGameView(current, incoming),
    )
  }

  const storeLiveView = (incoming: GameView) => {
    setNegotiationError(null)
    const baseline = audioBaselineVersion.current
    if (baseline === null || incoming.version > baseline) {
      const events = liveSoundEvents(incoming)
      if (events.length) audioService.emitLive(incoming.gameId, incoming.version, events)
    }
    audioBaselineVersion.current = Math.max(baseline ?? -1, incoming.version)
    queryClient.setQueryData<GameView>(queryKeys.game(gameId), (current) =>
      mergeGameView(current, incoming),
    )
  }

  const storeDrawOffer = (offer: DrawOffer) => {
    setNegotiationError(null)
    queryClient.setQueryData<GameView>(queryKeys.game(gameId), (current) =>
      mergeDrawOfferChange(current, offer),
    )
  }

  const storeTakebackRequest = (request: TakebackRequestView) => {
    setNegotiationError(null)
    queryClient.setQueryData<GameView>(queryKeys.game(gameId), (current) =>
      mergeTakebackRequestChange(current, request),
    )
  }

  const realtimeState = useGameHub({
    gameId,
    version: view?.version ?? 0,
    onView: storeLiveView,
    onSnapshot: storeRecoveryView,
    onDrawOffer: storeDrawOffer,
    onTakebackRequest: storeTakebackRequest,
    onOpponentConnection: setOpponentConnected,
    onReconnect: () => {
      void refetchGame()
    },
  })
  const interactionLocked = commandPending || realtimeState !== 'connected'

  const runCommand = (command: () => void) => {
    if (commandLock.current || realtimeState !== 'connected') return
    commandLock.current = true
    setCommandPending(true)
    setCommandError(null)
    command()
  }

  const runNegotiationCommand = (command: () => void) => {
    setNegotiationError(null)
    runCommand(command)
  }

  const releaseCommand = () => {
    commandLock.current = false
    setCommandPending(false)
  }

  const setNegotiationFailure = (error: unknown) => {
    const message = errorMessage(error)
    setNegotiationError(message)
    setCommandError(message)
  }

  useEffect(() => {
    const refreshAfterVisibilityChange = () => {
      if (document.visibilityState === 'visible') void refetchGame()
    }
    document.addEventListener('visibilitychange', refreshAfterVisibilityChange)
    return () => document.removeEventListener('visibilitychange', refreshAfterVisibilityChange)
  }, [refetchGame])

  useEffect(() => {
    if (!view || !interpolatedClock || view.status !== 'playing') return
    const totalRemaining = view.perspective === 'red'
      ? interpolatedClock.redMilliseconds
      : interpolatedClock.blackMilliseconds
    const remaining = view.sideToMove === view.perspective &&
      interpolatedClock.turnMilliseconds !== null
      ? Math.min(totalRemaining, interpolatedClock.turnMilliseconds)
      : totalRemaining
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
    if (!view) return
    const activeGameId = view.status === 'playing' ? view.gameId : null
    queryClient.setQueryData<GuestSession>(queryKeys.session, (session) => {
      if (!session || session.activeGameId === activeGameId) return session
      return { ...session, activeGameId }
    })
  }, [queryClient, view])

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
    onSuccess: storeLiveView,
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
      storeLiveView(updatedView)
      setConfirmResign(false)
    },
    onError: (error) => setCommandError(errorMessage(error)),
    onSettled: releaseCommand,
  })
  const offerDraw = useMutation({
    mutationFn: () => api.offerDraw(gameId),
    onSuccess: storeDrawOffer,
    onError: setNegotiationFailure,
    onSettled: releaseCommand,
  })
  const acceptDraw = useMutation({
    mutationFn: () => api.acceptDraw(gameId),
    onSuccess: storeLiveView,
    onError: setNegotiationFailure,
    onSettled: releaseCommand,
  })
  const rejectDraw = useMutation({
    mutationFn: () => api.rejectDraw(gameId),
    onSuccess: storeDrawOffer,
    onError: setNegotiationFailure,
    onSettled: releaseCommand,
  })
  const requestTakeback = useMutation({
    mutationFn: (expectedVersion: number) => api.createTakebackRequest(gameId, {
      expectedVersion,
      clientRequestId: createClientId(),
    }),
    onSuccess: storeTakebackRequest,
    onError: setNegotiationFailure,
    onSettled: releaseCommand,
  })
  const acceptTakeback = useMutation({
    mutationFn: (requestId: string) => api.acceptTakebackRequest(gameId, requestId),
    onSuccess: storeLiveView,
    onError: setNegotiationFailure,
    onSettled: releaseCommand,
  })
  const rejectTakeback = useMutation({
    mutationFn: (requestId: string) => api.rejectTakebackRequest(gameId, requestId),
    onSuccess: storeTakebackRequest,
    onError: setNegotiationFailure,
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
  const isFinished = view.status === 'finished'
  const captured = view.captureSummary
  const negotiationPending = Boolean(view.drawOffer || view.takebackRequest)
  const opponentSide: Side = view.perspective === 'red' ? 'black' : 'red'
  const totalForSide = (side: Side) => side === 'red'
    ? interpolatedClock?.redMilliseconds ?? 0
    : interpolatedClock?.blackMilliseconds ?? 0
  const effectiveForSide = (side: Side) => {
    const total = totalForSide(side)
    return view.status === 'playing' && view.sideToMove === side && interpolatedClock?.turnMilliseconds != null
      ? Math.min(total, interpolatedClock.turnMilliseconds)
      : total
  }
  const clockBar = (side: Side, relationship: 'self' | 'opponent') => interpolatedClock ? (
    <PlayerClockBar
      side={side}
      relationship={relationship}
      totalMilliseconds={totalForSide(side)}
      turnMilliseconds={view.sideToMove === side ? interpolatedClock.turnMilliseconds : null}
      active={view.status === 'playing' && view.sideToMove === side}
      low={effectiveForSide(side) < 10_000}
    />
  ) : null

  return (
    <div className="game-page">
      <header className="game-header game-header--compact">
        <h1>{isFinished ? '棋局结束' : myTurn ? '轮到你行棋' : `等待${sideNames[view.sideToMove]}行棋`}</h1>
      </header>
      <p className="sr-only" role="status" aria-live="polite">{realtimeLabels[realtimeState]}</p>

      {!opponentConnected && !isFinished ? (
        <div className="notice-banner" role="status">对手暂时离线；棋局会在其使用原会话重连后继续。</div>
      ) : null}
      {commandError ? <div className="notice-banner notice-banner--error" role="alert">{commandError}</div> : null}

      <div className="game-layout">
        <section className="board-column">
          {clockBar(opponentSide, 'opponent')}
          {!interpolatedClock ? (
            <section className="turn-card turn-card--board">
              <span className={`side-token side-token--${view.sideToMove}`} aria-hidden="true">
                {view.sideToMove === 'red' ? '帅' : '将'}
              </span>
              <div><small>当前行棋</small><strong>{sideNames[view.sideToMove]}</strong></div>
              <span className={myTurn ? 'turn-badge turn-badge--mine' : 'turn-badge'}>{myTurn ? '你的回合' : '对方回合'}</span>
            </section>
          ) : null}
          <div className="game-board-stage">
            <GameBoard
              view={view}
              interactionLocked={interactionLocked}
              onMove={(from, to) =>
                runCommand(() => move.mutate({ from, to, version: view.version }))
              }
            />
            <GameNegotiationOverlay
              view={view}
              submitting={commandPending}
              locked={interactionLocked}
              error={negotiationError}
              onAcceptDraw={() => runNegotiationCommand(() => acceptDraw.mutate())}
              onRejectDraw={() => runNegotiationCommand(() => rejectDraw.mutate())}
              onAcceptTakeback={() => {
                const requestId = view.takebackRequest?.id
                if (requestId) runNegotiationCommand(() => acceptTakeback.mutate(requestId))
              }}
              onRejectTakeback={() => {
                const requestId = view.takebackRequest?.id
                if (requestId) runNegotiationCommand(() => rejectTakeback.mutate(requestId))
              }}
            />
          </div>
          {clockBar(view.perspective, 'self')}
          <div className="board-legend">
            <span><i className="legend-swatch legend-swatch--fog" />未知区域</span>
            <span><i className="legend-swatch legend-swatch--visible" />当前可见</span>
            <span><i className="legend-swatch legend-swatch--move" />候选落点</span>
          </div>
        </section>

        <aside className="game-sidebar" aria-label="棋局状态与操作">
          {isFinished && view.result ? (
            <section className="result-card" role="status">
              <p className="page-kicker">FINAL RESULT</p>
              <h2>{view.result.winner ? `${sideNames[view.result.winner]}获胜` : '本局和棋'}</h2>
              <p>{resultReasons[view.result.reason]}</p>
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
              {!negotiationPending ? (
                <div className="game-negotiation-actions" aria-label="协商操作">
                  <button
                    type="button"
                    className="button button--secondary game-negotiation-actions__draw"
                    disabled={interactionLocked}
                    onClick={() => runNegotiationCommand(() => offerDraw.mutate())}
                  >
                    {offerDraw.isPending ? '正在发送…' : '提议和棋'}
                  </button>
                  {view.canRequestTakeback ? (
                    <button
                      type="button"
                      className="button button--secondary game-negotiation-actions__takeback"
                      disabled={interactionLocked}
                      onClick={() => runNegotiationCommand(() => requestTakeback.mutate(view.version))}
                    >
                      {requestTakeback.isPending ? '正在发送…' : '请求悔棋'}
                    </button>
                  ) : null}
                </div>
              ) : null}

              {confirmResign ? (
                <div className="confirm-action" role="alertdialog" aria-label="确认认输">
                  <p>认输后不能撤销，确定结束本局？</p>
                  <div>
                    <button type="button" className="button button--danger" disabled={interactionLocked} onClick={() => runCommand(() => resign.mutate())}>
                      {resign.isPending ? '正在提交…' : '确认认输'}
                    </button>
                    <button type="button" className="button button--secondary" disabled={commandPending} onClick={() => setConfirmResign(false)}>继续对局</button>
                  </div>
                </div>
              ) : (
                <button type="button" className="text-danger" disabled={interactionLocked} onClick={() => setConfirmResign(true)}>认输</button>
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
