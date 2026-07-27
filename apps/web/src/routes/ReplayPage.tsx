import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useMemo, useState } from 'react'
import { Link, useParams } from 'react-router'
import { api, errorMessage } from '../api/client'
import { queryKeys } from '../api/queryKeys'
import { createClientId } from '../api/types'
import type {
  GameResult,
  GameView,
  GuestSession,
  HistoricalReplay,
  PieceType,
  Side,
} from '../api/types'
import { ErrorPanel, PageLoader } from '../components/AppShell'
import { GameBoard } from '../components/board/GameBoard'

type ReplayMode = 'red' | 'black' | 'omniscient'

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
}
const pieceNames: Record<Side, Record<PieceType, string>> = {
  red: { general: '帅', advisor: '仕', elephant: '相', horse: '马', rook: '车', cannon: '炮', pawn: '兵' },
  black: { general: '将', advisor: '士', elephant: '象', horse: '马', rook: '车', cannon: '炮', pawn: '卒' },
}

function useOpaqueTokenKey(token: string, enabled: boolean) {
  return useMemo(
    () => enabled && token ? createClientId() : '',
    [enabled, token],
  )
}

function formatStaticClock(milliseconds: number) {
  const seconds = Math.max(0, Math.ceil(milliseconds / 1_000))
  return `${Math.floor(seconds / 60).toString().padStart(2, '0')}:${(seconds % 60)
    .toString()
    .padStart(2, '0')}`
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
  const [mode, setMode] = useState<ReplayMode>('omniscient')
  const [playing, setPlaying] = useState(false)
  const [sharePath, setSharePath] = useState('')
  const [copied, setCopied] = useState(false)
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

  useEffect(() => {
    if (!replay) return
    setFrameIndex(0)
    setMode(replay.currentPlayerSide ?? 'omniscient')
    setPlaying(false)
  }, [replay])

  useEffect(() => {
    if (!playing || !replay) return
    const timer = window.setInterval(() => {
      setFrameIndex((current) => {
        if (current >= replay.frames.length - 1) {
          setPlaying(false)
          return current
        }
        return current + 1
      })
    }, 900)
    return () => window.clearInterval(timer)
  }, [playing, replay])

  useEffect(() => {
    const handleKey = (event: KeyboardEvent) => {
      if (!replay || event.target instanceof HTMLInputElement) return
      if (event.key === 'ArrowLeft') {
        event.preventDefault()
        setFrameIndex((current) => Math.max(0, current - 1))
      } else if (event.key === 'ArrowRight') {
        event.preventDefault()
        setFrameIndex((current) => Math.min(replay.frames.length - 1, current + 1))
      }
    }
    window.addEventListener('keydown', handleKey)
    return () => window.removeEventListener('keydown', handleKey)
  }, [replay])

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
  const projection = frame.views[mode]
  const perspective: Side = mode === 'black' ? 'black' : 'red'
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
    ratingChange: null,
  }
  const move = projection.move
  const shareUrl = sharePath ? new URL(sharePath, window.location.origin).toString() : ''
  const generateShare = () => {
    if (sharePath && !window.confirm('重新生成会立即使旧链接失效，是否继续？')) return
    createShare.mutate()
  }
  const copyShare = async () => {
    await navigator.clipboard.writeText(shareUrl)
    setCopied(true)
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
      </section>

      <div className="replay-mode-switch" role="group" aria-label="回放视野">
        {([
          ['red', '红方视野'],
          ['black', '黑方视野'],
          ['omniscient', '全局视野'],
        ] as const).map(([value, label]) => (
          <button
            type="button"
            key={value}
            aria-pressed={mode === value}
            onClick={() => setMode(value)}
          >
            {label}
          </button>
        ))}
      </div>

      <div className="replay-layout">
        <section className="board-column">
          <GameBoard
            view={boardView}
            replayLabel={`${mode === 'omniscient' ? '全局' : sideNames[mode]}视野，第 ${frame.ply} 个半回合`}
          />
        </section>

        <aside className="replay-controls" aria-label="回放控制">
          <div className="replay-counter">
            <small>当前进度</small>
            <strong>{safeIndex}<span> / {replay.frames.length - 1}</span></strong>
          </div>
          <div className="replay-move">
            {move ? (
              <>
                <span className={`side-token side-token--${move.side}`} aria-hidden="true">
                  {pieceNames[move.side][move.piece]}
                </span>
                <div>
                  <strong>{sideNames[move.side]}{pieceNames[move.side][move.piece]}</strong>
                  <p>{move.from.file + 1}路{move.from.rank + 1}线 → {move.to.file + 1}路{move.to.rank + 1}线</p>
                  {move.captured ? <small>吃 {pieceNames[move.side === 'red' ? 'black' : 'red'][move.captured]}</small> : null}
                </div>
              </>
            ) : (
              <div>
                <strong>{safeIndex === 0 ? '初始局面' : '对手走子或终局事件'}</strong>
                <p>{mode === 'omniscient' ? '当前帧没有走子坐标' : '侧方视野不会显示对手原始走子坐标'}</p>
              </div>
            )}
          </div>
          {frame.clock ? (
            <div className="replay-clock">
              <span>红 {formatStaticClock(frame.clock.redMilliseconds)}</span>
              <span>黑 {formatStaticClock(frame.clock.blackMilliseconds)}</span>
            </div>
          ) : null}
          <input
            type="range"
            min="0"
            max={replay.frames.length - 1}
            value={safeIndex}
            onChange={(event) => setFrameIndex(Number(event.target.value))}
            aria-label="回放进度"
          />
          <div className="replay-buttons">
            <button type="button" aria-label="回到开局" disabled={safeIndex === 0} onClick={() => setFrameIndex(0)}>«</button>
            <button type="button" aria-label="上一步" disabled={safeIndex === 0} onClick={() => setFrameIndex(safeIndex - 1)}>‹</button>
            <button type="button" aria-label={playing ? '暂停播放' : '开始播放'} onClick={() => setPlaying((value) => !value)}>
              {playing ? 'Ⅱ' : '▶'}
            </button>
            <button type="button" aria-label="下一步" disabled={safeIndex === replay.frames.length - 1} onClick={() => setFrameIndex(safeIndex + 1)}>›</button>
            <button type="button" aria-label="跳到终局" disabled={safeIndex === replay.frames.length - 1} onClick={() => setFrameIndex(replay.frames.length - 1)}>»</button>
          </div>
        </aside>
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
