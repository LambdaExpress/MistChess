import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { Link, useParams } from 'react-router'
import { api, errorMessage } from '../api/client'
import { queryKeys } from '../api/queryKeys'
import type { GameView, PieceType, Side } from '../api/types'
import { ErrorPanel, PageLoader } from '../components/AppShell'
import { GameBoard } from '../components/board/GameBoard'

const sideNames: Record<Side, string> = { red: '红方', black: '黑方' }
const pieceNames: Record<Side, Record<PieceType, string>> = {
  red: { general: '帅', advisor: '仕', elephant: '相', horse: '马', rook: '车', cannon: '炮', pawn: '兵' },
  black: { general: '将', advisor: '士', elephant: '象', horse: '马', rook: '车', cannon: '炮', pawn: '卒' },
}

export function ReplayPage() {
  const { gameId = '' } = useParams<{ gameId: string }>()
  const [frameIndex, setFrameIndex] = useState(0)
  const replayQuery = useQuery({
    queryKey: queryKeys.replay(gameId),
    queryFn: () => api.getReplay(gameId),
    enabled: gameId.length > 0,
    staleTime: Number.POSITIVE_INFINITY,
    retry: 1,
  })

  if (!gameId) return <ErrorPanel title="回放编号无效" detail="请从已结束棋局进入回放。" />
  if (replayQuery.isPending) return <PageLoader label="正在载入完整棋局回放…" />
  if (replayQuery.isError) {
    return (
      <ErrorPanel
        title="暂时无法查看回放"
        detail={errorMessage(replayQuery.error)}
        onRetry={() => void replayQuery.refetch()}
      />
    )
  }

  const replay = replayQuery.data
  if (!replay.frames.length) {
    return <ErrorPanel title="回放数据不完整" detail="服务器未返回任何棋局帧。" />
  }
  const safeIndex = Math.min(frameIndex, replay.frames.length - 1)
  const frame = replay.frames[safeIndex]
  const allSquares = Array.from({ length: 90 }, (_, index) => ({
    file: index % 9,
    rank: Math.floor(index / 9),
  }))
  const boardView: GameView = {
    gameId: replay.gameId,
    ruleVersion: replay.ruleVersion,
    version: frame.ply,
    status: 'finished',
    result: replay.result,
    perspective: replay.perspective,
    sideToMove: frame.sideToMove,
    visibleSquares: allSquares,
    pieces: frame.pieces,
    candidateMoves: [],
    captureSummary: { redLost: [], blackLost: [] },
    clock: null,
    drawOffer: null,
  }
  const move = frame.move

  return (
    <div className="replay-page">
      <header className="replay-header">
        <div>
          <p className="page-kicker">FULL REPLAY · {replay.ruleVersion}</p>
          <h1>完整棋局回放</h1>
          <p>对局结束后公开全部棋盘，不再应用迷雾遮罩。</p>
        </div>
        <Link to={`/game/${encodeURIComponent(gameId)}`} className="button button--secondary">返回终局</Link>
      </header>

      <div className="replay-layout">
        <section className="board-column">
          <GameBoard view={boardView} replayLabel={`完整回放，第 ${frame.ply} 个半回合`} />
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
              <div><strong>初始局面</strong><p>尚未走子</p></div>
            )}
          </div>
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
            <button type="button" aria-label="下一步" disabled={safeIndex === replay.frames.length - 1} onClick={() => setFrameIndex(safeIndex + 1)}>›</button>
            <button type="button" aria-label="跳到终局" disabled={safeIndex === replay.frames.length - 1} onClick={() => setFrameIndex(replay.frames.length - 1)}>»</button>
          </div>
          <div className="replay-result">
            <span>最终结果</span>
            <strong>{replay.result.winner ? `${sideNames[replay.result.winner]}获胜` : '和棋'}</strong>
          </div>
        </aside>
      </div>
    </div>
  )
}
