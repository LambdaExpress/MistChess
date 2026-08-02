import { useQuery } from '@tanstack/react-query'
import { useEffect, useRef, useState } from 'react'
import { Link, useParams } from 'react-router'
import { api, errorMessage } from '../../api/client'
import { queryKeys } from '../../api/queryKeys'
import { createClientId } from '../../api/types'
import type {
  AdminReplay,
  GameView,
  Side,
} from '../../api/types'
import { ErrorPanel, PageLoader } from '../../components/AppShell'
import { GameBoard } from '../../components/board/GameBoard'
import { ReplayStepControls } from '../../components/board/ReplayStepControls'
import { audioService } from '../../features/audio/audioService'
import type { SoundEvent } from '../../features/audio/audioService'

type VisibilityMode = 'red' | 'black' | 'omniscient'

const sideNames: Record<Side, string> = { red: '红方', black: '黑方' }
const outcomeNames: Record<AdminReplay['red']['outcome'], string> = {
  win: '胜',
  loss: '负',
  draw: '和',
}
const reasonNames: Record<string, string> = {
  generalCaptured: '将帅被吃',
  noLegalMove: '无合法走法',
  resignation: '认输',
  timeout: '超时',
  agreedDraw: '协议和棋',
  repetition: '重复局面和棋',
  noProgress: '无进展和棋',
  administrativeForfeit: '管理员封禁判负',
}

export function AdminReplayPage() {
  const { gameId = '' } = useParams<{ gameId: string }>()
  const [frameIndex, setFrameIndex] = useState(0)
  const [visibilityMode, setVisibilityMode] = useState<VisibilityMode>('omniscient')
  const [orientation, setOrientation] = useState<Side>('red')
  const replayAudioSession = useRef(createClientId()).current
  const replayStepSequence = useRef(0)
  const replayQuery = useQuery({
    queryKey: queryKeys.adminReplay(gameId),
    queryFn: () => api.getAdminReplay(gameId),
    enabled: gameId.length > 0,
    retry: false,
    staleTime: Number.POSITIVE_INFINITY,
  })
  const replay = replayQuery.data
  const replayIdentity = replay?.gameId

  useEffect(() => {
    if (!replayIdentity) return
    setFrameIndex(0)
    setVisibilityMode('omniscient')
    setOrientation('red')
    replayStepSequence.current = 0
  }, [replayIdentity])

  if (!gameId) {
    return <ErrorPanel title="棋局编号无效" detail="请从管理员用户详情页进入回放。" />
  }
  if (replayQuery.isPending) {
    return <PageLoader label="正在载入管理员三视野回放…" />
  }
  if (replayQuery.isError || !replay) {
    return (
      <ErrorPanel
        title="暂时无法查看管理员回放"
        detail={errorMessage(replayQuery.error)}
        onRetry={() => void replayQuery.refetch()}
      />
    )
  }
  if (!replay.frames.length) {
    return <ErrorPanel title="回放数据不完整" detail="服务器未返回任何棋局帧。" />
  }

  const safeIndex = Math.min(frameIndex, replay.frames.length - 1)
  const frame = replay.frames[safeIndex]
  const projection = frame.views[visibilityMode]
  const perspective: Side = visibilityMode === 'black' ? 'black' : 'red'
  const boardView: GameView = {
    gameId: replay.gameId,
    ruleVersion: replay.ruleVersion,
    timeControl: replay.timeControl,
    version: frame.ply,
    status: 'finished',
    result: replay.result,
    perspective,
    sideToMove: frame.sideToMove,
    visibleSquares: projection.visibleSquares,
    pieces: projection.pieces,
    candidateMoves: [],
    captureSummary: projection.captureSummary,
    clock: frame.clock,
    drawOffer: null,
    negotiationVersion: 0,
    takebackRequest: null,
    lastAction: null,
    canRequestTakeback: false,
  }
  const previousFrame = () => setFrameIndex(Math.max(0, safeIndex - 1))
  const nextFrame = () => {
    if (safeIndex >= replay.frames.length - 1) return
    const nextIndex = safeIndex + 1
    const targetFrame = replay.frames[nextIndex]
    const move = targetFrame.views.omniscient.move
    const isTerminalFrame = nextIndex === replay.frames.length - 1
    const events: SoundEvent[] = []
    if (move?.captured) {
      events.push('capture')
    } else if (move && !isTerminalFrame) {
      events.push('move-opponent')
    }
    if (isTerminalFrame) {
      events.push(replay.result.winner === null ? 'game-draw' : 'game-win')
    }
    setFrameIndex(nextIndex)
    replayStepSequence.current += 1
    audioService.emitReplay(`${replayAudioSession}:${replay.gameId}`, replayStepSequence.current, events)
  }
  return (
    <div className="admin-page admin-replay-page">
      <div className="admin-breadcrumbs" aria-label="面包屑">
        <Link to="/admin/users">用户管理</Link>
        <span aria-hidden="true">/</span>
        <span>棋局回放</span>
      </div>

      <header className="admin-page-header admin-replay-header">
        <div>
          <p className="admin-kicker">AUTHORIZED REPLAY</p>
          <h1>管理员棋局回放</h1>
          <p>切换红方、黑方与全局视野。此只读入口不会创建或暴露公开分享链接。</p>
        </div>
        <code>{replay.gameId}</code>
      </header>

      <section className="admin-replay-meta" aria-label="棋局结果">
        <div>
          <span className="admin-side-token admin-side-token--red" aria-hidden="true">帅</span>
          <strong>{replay.red.displayName}</strong>
          <em data-outcome={replay.red.outcome}>{outcomeNames[replay.red.outcome]}</em>
        </div>
        <span>对</span>
        <div>
          <span className="admin-side-token admin-side-token--black" aria-hidden="true">将</span>
          <strong>{replay.black.displayName}</strong>
          <em data-outcome={replay.black.outcome}>{outcomeNames[replay.black.outcome]}</em>
        </div>
        <p>{reasonNames[replay.result.reason] ?? replay.result.reason}</p>
        <p>
          {replay.ruleVersion} · {replay.timeControl ?? '无计时'}
          {replay.moveTimeLimitSeconds ? ` · 每步 ${replay.moveTimeLimitSeconds} 秒` : ''}
        </p>
      </section>

      <div className="admin-replay-modes" role="group" aria-label="信息视野">
        {([
          ['red', '红方视野'],
          ['black', '黑方视野'],
          ['omniscient', '全局视野'],
        ] as const).map(([value, label]) => (
          <button
            type="button"
            key={value}
            aria-pressed={visibilityMode === value}
            onClick={() => setVisibilityMode(value)}
          >
            {label}
          </button>
        ))}
      </div>

      <div className="admin-replay-modes admin-replay-orientation" role="group" aria-label="棋盘朝向">
        {([
          ['red', '红方在下'],
          ['black', '黑方在下'],
        ] as const).map(([value, label]) => (
          <button
            type="button"
            key={value}
            aria-pressed={orientation === value}
            onClick={() => setOrientation(value)}
          >
            {label}
          </button>
        ))}
      </div>

      <div className="admin-replay-layout">
        <section className="admin-replay-board">
          <GameBoard
            view={boardView}
            orientation={orientation}
            omniscientVisibility={visibilityMode === 'omniscient' ? {
              red: frame.views.red.visibleSquares,
              black: frame.views.black.visibleSquares,
            } : undefined}
            replayLabel={`${visibilityMode === 'omniscient' ? '全局' : sideNames[visibilityMode]}视野，${sideNames[orientation]}在下，第 ${frame.ply} 个半回合`}
          />
          <aside className="admin-replay-tools" aria-label="回放辅助信息与控制">
            {visibilityMode === 'omniscient' ? (
              <div className="board-legend admin-replay-visibility-legend" aria-label="全局视野图例">
                <span><i className="legend-swatch legend-swatch--red-blind" />红方盲区</span>
                <span><i className="legend-swatch legend-swatch--black-blind" />黑方盲区</span>
                <span><i className="legend-swatch legend-swatch--both-blind" />双方盲区</span>
                <span><i className="legend-swatch legend-swatch--visible" />双方可见</span>
              </div>
            ) : null}
            <ReplayStepControls
              current={safeIndex}
              total={replay.frames.length - 1}
              onPrevious={previousFrame}
              onNext={nextFrame}
              className="admin-replay-buttons"
            />
          </aside>

        </section>
      </div>
    </div>
  )
}
