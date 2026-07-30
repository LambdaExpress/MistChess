import { useQuery } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { Link, useParams } from 'react-router'
import { api, errorMessage } from '../../api/client'
import { queryKeys } from '../../api/queryKeys'
import type {
  AdminReplay,
  GameView,
  PieceType,
  Side,
} from '../../api/types'
import { ErrorPanel, PageLoader } from '../../components/AppShell'
import { GameBoard } from '../../components/board/GameBoard'

type ReplayMode = 'red' | 'black' | 'omniscient'

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
const pieceNames: Record<Side, Record<PieceType, string>> = {
  red: { general: '帅', advisor: '仕', elephant: '相', horse: '马', rook: '车', cannon: '炮', pawn: '兵' },
  black: { general: '将', advisor: '士', elephant: '象', horse: '马', rook: '车', cannon: '炮', pawn: '卒' },
}

export function AdminReplayPage() {
  const { gameId = '' } = useParams<{ gameId: string }>()
  const [frameIndex, setFrameIndex] = useState(0)
  const [mode, setMode] = useState<ReplayMode>('omniscient')
  const [playing, setPlaying] = useState(false)
  const replayQuery = useQuery({
    queryKey: queryKeys.adminReplay(gameId),
    queryFn: () => api.getAdminReplay(gameId),
    enabled: gameId.length > 0,
    retry: false,
    staleTime: Number.POSITIVE_INFINITY,
  })
  const replay = replayQuery.data

  useEffect(() => {
    if (!replay) return
    setFrameIndex(0)
    setMode('omniscient')
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
  }
  const move = projection.move

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

      <div className="admin-replay-modes" role="group" aria-label="回放视野">
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

      <div className="admin-replay-layout">
        <section className="admin-replay-board">
          <GameBoard
            view={boardView}
            replayLabel={`${mode === 'omniscient' ? '全局' : sideNames[mode]}视野，第 ${frame.ply} 个半回合`}
          />
        </section>

        <aside className="admin-replay-controls" aria-label="回放控制">
          <div className="admin-replay-counter">
            <small>当前进度</small>
            <strong>{safeIndex}<span> / {replay.frames.length - 1}</span></strong>
          </div>
          <div className="admin-replay-move">
            {move ? (
              <>
                <span className={`admin-side-token admin-side-token--${move.side}`} aria-hidden="true">
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
            <div className="admin-replay-clock">
              <span>红 {Math.ceil(frame.clock.redMilliseconds / 1_000)} 秒</span>
              <span>黑 {Math.ceil(frame.clock.blackMilliseconds / 1_000)} 秒</span>
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
          <div className="admin-replay-buttons">
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
    </div>
  )
}
