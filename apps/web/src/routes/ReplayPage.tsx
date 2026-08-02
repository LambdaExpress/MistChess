import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useMemo, useRef, useState } from 'react'
import { Link, useParams } from 'react-router'
import { api, errorMessage } from '../api/client'
import { queryKeys } from '../api/queryKeys'
import { createClientId } from '../api/types'
import type {
  GameResult,
  GameView,
  GuestSession,
  HistoricalReplay,
  Side,
} from '../api/types'
import { ErrorPanel, PageLoader } from '../components/AppShell'
import { GameBoard } from '../components/board/GameBoard'
import { ReplayStepControls } from '../components/board/ReplayStepControls'
import { audioService } from '../features/audio/audioService'
import type { SoundEvent } from '../features/audio/audioService'

type VisibilityMode = 'red' | 'black' | 'omniscient'

const sideNames: Record<Side, string> = { red: '红方', black: '黑方' }
const outcomeNames: Record<HistoricalReplay['red']['outcome'], string> = {
  win: '胜',
  loss: '负',
  draw: '和',
}
const reasonNames: Record<GameResult['reason'], string> = {
  generalCaptured: '将帅被吃',
  noLegalMove: '无合法走法',
  resignation: '认输',
  timeout: '超时',
  agreedDraw: '协议和棋',
  repetition: '重复局面和棋',
  noProgress: '无进展和棋',
  administrativeForfeit: '管理员判负',
}

function useOpaqueTokenKey(token: string, enabled: boolean) {
  return useMemo(
    () => enabled && token ? createClientId() : '',
    [enabled, token],
  )
}


export function ReplayPage({ shared = false }: { shared?: boolean }) {
  const { gameId = '', shareToken = '' } = useParams<{
    gameId: string
    shareToken: string
  }>()
  const queryClient = useQueryClient()
  const session = queryClient.getQueryData<GuestSession>(queryKeys.session)
  const opaqueTokenKey = useOpaqueTokenKey(shareToken, shared)
  const [frameIndex, setFrameIndex] = useState(0)
  const [visibilityMode, setVisibilityMode] = useState<VisibilityMode>('omniscient')
  const [orientation, setOrientation] = useState<Side>('red')
  const [sharePath, setSharePath] = useState('')
  const [copied, setCopied] = useState(false)
  const replayAudioSession = useRef(createClientId()).current
  const replayStepSequence = useRef(0)
  const replayKey = shared
    ? queryKeys.sharedReplay(opaqueTokenKey)
    : queryKeys.privateReplay(session?.playerId ?? '', gameId)
  const replayQuery = useQuery({
    queryKey: replayKey,
    queryFn: () => shared ? api.getSharedReplay(shareToken) : api.getReplay(gameId),
    enabled: shared
      ? shareToken.length > 0 && opaqueTokenKey.length > 0
      : gameId.length > 0 && Boolean(session?.playerId),
    staleTime: Number.POSITIVE_INFINITY,
  })
  const replay = replayQuery.data
  const replayIdentity = replay?.gameId
  const defaultSide = replay?.currentPlayerSide

  useEffect(() => {
    if (!replayIdentity) return
    setFrameIndex(0)
    setVisibilityMode(defaultSide ?? 'omniscient')
    setOrientation(defaultSide ?? 'red')
    replayStepSequence.current = 0
  }, [defaultSide, replayIdentity])

  const createShare = useMutation({
    mutationFn: () => api.createReplayShare(gameId),
    onSuccess: (created) => setSharePath(created.sharePath),
  })
  const revokeShare = useMutation({
    mutationFn: () => api.revokeReplayShare(gameId),
    onSuccess: () => {
      setSharePath('')
      setCopied(false)
    },
  })

  if ((!shared && !gameId) || (shared && !shareToken)) {
    return <ErrorPanel title="回放编号无效" detail="请使用有效的历史记录或分享链接。" />
  }
  if (replayQuery.isPending) return <PageLoader label="正在载入三种视野回放…" />
  if (replayQuery.isError || !replay) {
    return (
      <ErrorPanel
        title={shared ? '分享链接无效或已撤销' : '暂时无法查看回放'}
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
  const shareUrl = sharePath ? new URL(sharePath, window.location.origin).toString() : ''
  const generateShare = () => {
    if (sharePath && !window.confirm('重新生成会立即使旧链接失效，是否继续？')) return
    createShare.mutate()
  }
  const copyShare = async () => {
    await navigator.clipboard.writeText(shareUrl)
    setCopied(true)
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
      events.push(replay.result.winner === null
        ? 'game-draw'
        : replay.currentPlayerSide === null || replay.result.winner === replay.currentPlayerSide
          ? 'game-win'
          : 'game-loss')
    }
    setFrameIndex(nextIndex)
    replayStepSequence.current += 1
    audioService.emitReplay(`${replayAudioSession}:${replay.gameId}`, replayStepSequence.current, events)
  }
  return (
    <div className="replay-page">
      <header className="replay-header">
        <div>
          <p className="page-kicker">
            {shared ? 'SHARED REPLAY · 通过分享链接观看' : 'PRIVATE REPLAY · 我的历史对局'}
          </p>
          <h1>迷雾棋局回放</h1>
          <p>视野模式复现该方当时可见的信息；全局视野显示完整棋盘。</p>
        </div>
        {!shared ? <Link to="/history" className="button button--secondary">返回历史列表</Link> : null}
      </header>

      <section className="replay-meta" aria-label="棋局结果">
        <div>
          <span className="side-token side-token--red" aria-hidden="true">帅</span>
          <strong>{replay.red.displayName}</strong>
          <em data-outcome={replay.red.outcome}>{outcomeNames[replay.red.outcome]}</em>
        </div>
        <span>对</span>
        <div>
          <span className="side-token side-token--black" aria-hidden="true">将</span>
          <strong>{replay.black.displayName}</strong>
          <em data-outcome={replay.black.outcome}>{outcomeNames[replay.black.outcome]}</em>
        </div>
        <p>{reasonNames[replay.result.reason]}</p>
        <p>
          {replay.timeControl
            ? `${replay.timeControl}${replay.moveTimeLimitSeconds ? ` · 每步 ${replay.moveTimeLimitSeconds} 秒` : ''}`
            : '无计时'}
        </p>
      </section>

      <div className="replay-mode-switch" role="group" aria-label="信息视野">
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

      <div className="replay-mode-switch replay-orientation-switch" role="group" aria-label="棋盘朝向">
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

      <div className="replay-layout">
        <section className="board-column">
          <GameBoard
            view={boardView}
            orientation={orientation}
            omniscientVisibility={visibilityMode === 'omniscient' ? {
              red: frame.views.red.visibleSquares,
              black: frame.views.black.visibleSquares,
            } : undefined}
            replayLabel={`${visibilityMode === 'omniscient' ? '全局' : sideNames[visibilityMode]}视野，${sideNames[orientation]}在下，第 ${frame.ply} 个半回合`}
          />
          {visibilityMode === 'omniscient' ? (
            <div className="board-legend replay-visibility-legend" aria-label="全局视野图例">
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
            className="replay-buttons"
          />
        </section>
      </div>

      {!shared ? (
        <section className="share-panel">
          <div>
            <h2>分享这局回放</h2>
            <p>获得链接的人都能观看这一局；重新生成或撤销后，旧链接立即失效。</p>
          </div>
          <button
            type="button"
            className="button button--secondary"
            disabled={createShare.isPending}
            onClick={generateShare}
          >
            {createShare.isPending ? '正在生成…' : sharePath ? '重新生成链接' : '生成分享链接'}
          </button>
          {sharePath ? (
            <div className="share-link">
              <input value={shareUrl} readOnly aria-label="分享链接" />
              <button type="button" className="button button--accent" onClick={() => void copyShare()}>
                {copied ? '已复制' : '复制链接'}
              </button>
            </div>
          ) : null}
          <button
            type="button"
            className="text-danger"
            disabled={revokeShare.isPending}
            onClick={() => revokeShare.mutate()}
          >
            {revokeShare.isPending ? '正在撤销…' : '撤销当前分享'}
          </button>
          {createShare.isError || revokeShare.isError ? (
            <p className="inline-error" role="alert">
              {errorMessage(createShare.error ?? revokeShare.error)}
            </p>
          ) : null}
        </section>
      ) : (
        <section className="share-notice">
          通过分享链接观看。此链接只授予当前棋局的只读回放权限。
        </section>
      )}
    </div>
  )
}
