import { useEffect, useMemo, useState } from 'react'
import type { GameView, PieceType, PieceView, Position, Side } from '../../api/types'
import { positionKey, samePosition, toDisplayPosition } from './coordinates'

interface GameBoardProps {
  view: GameView
  interactionLocked?: boolean
  onMove?: (from: Position, to: Position) => void
  replayLabel?: string
}

interface InteractiveSquare {
  position: Position
  displayFile: number
  displayRank: number
  kind: 'piece' | 'destination'
  label: string
}

const pieceNames: Record<Side, Record<PieceType, string>> = {
  red: {
    general: '帅',
    advisor: '仕',
    elephant: '相',
    horse: '马',
    rook: '车',
    cannon: '炮',
    pawn: '兵',
  },
  black: {
    general: '将',
    advisor: '士',
    elephant: '象',
    horse: '马',
    rook: '车',
    cannon: '炮',
    pawn: '卒',
  },
}

const sideNames: Record<Side, string> = { red: '红方', black: '黑方' }
const allPositions = Array.from({ length: 90 }, (_, index) => ({
  file: index % 9,
  rank: Math.floor(index / 9),
}))

function boardPoint(position: Position, perspective: Side) {
  const display = toDisplayPosition(position, perspective)
  return {
    x: 71.5 + display.file * 55,
    y: 44 + (9 - display.rank) * 55,
    ...display,
  }
}

function squareLabel(position: Position): string {
  return `${position.file + 1}路、${position.rank + 1}线`
}

function PieceShape({ piece, perspective }: { piece: PieceView; perspective: Side }) {
  const point = boardPoint(piece.position, perspective)
  const name = pieceNames[piece.side][piece.type]
  const ariaLabel = `${sideNames[piece.side]}${name}，位于${squareLabel(piece.position)}`

  return (
    <g
      className={`board-piece board-piece--${piece.side}`}
      transform={`translate(${point.x} ${point.y})`}
      role="img"
      aria-label={ariaLabel}
      data-testid={`piece-${positionKey(piece.position)}`}
    >
      {piece.side === 'red' ? (
        <>
          <circle r="22" className="board-piece__body" />
          <circle r="17.5" className="board-piece__inner" />
        </>
      ) : (
        <>
          <polygon
            points="-15.5,-15.5 15.5,-15.5 22,0 15.5,15.5 -15.5,15.5 -22,0"
            className="board-piece__body"
          />
          <circle r="17.5" className="board-piece__inner" />
        </>
      )}
      <text textAnchor="middle" dominantBaseline="central" aria-hidden="true">
        {name}
      </text>
    </g>
  )
}

export function GameBoard({
  view,
  interactionLocked = false,
  onMove,
  replayLabel,
}: GameBoardProps) {
  const [selected, setSelected] = useState<Position | null>(null)
  const isPlayerTurn =
    view.status === 'playing' && view.sideToMove === view.perspective

  useEffect(() => {
    setSelected(null)
  }, [view.version])

  const visibleKeys = useMemo(
    () => new Set(view.visibleSquares.map(positionKey)),
    [view.visibleSquares],
  )
  const piecesByPosition = useMemo(
    () => new Map(view.pieces.map((piece) => [positionKey(piece.position), piece])),
    [view.pieces],
  )
  const destinations = useMemo(() => {
    if (!selected) return []
    return (
      view.candidateMoves.find((candidate) => samePosition(candidate.from, selected))
        ?.destinations ?? []
    )
  }, [selected, view.candidateMoves])
  const destinationKeys = useMemo(
    () => new Set(destinations.map(positionKey)),
    [destinations],
  )

  const interactiveSquares = useMemo(() => {
    const squares: InteractiveSquare[] = []
    for (const piece of view.pieces) {
      if (piece.side !== view.perspective) continue
      const display = toDisplayPosition(piece.position, view.perspective)
      squares.push({
        position: piece.position,
        displayFile: display.file,
        displayRank: display.rank,
        kind: 'piece',
        label: `选择${sideNames[piece.side]}${pieceNames[piece.side][piece.type]}，${squareLabel(piece.position)}`,
      })
    }
    if (selected) {
      const selectedPiece = piecesByPosition.get(positionKey(selected))
      for (const destination of destinations) {
        const display = toDisplayPosition(destination, view.perspective)
        squares.push({
          position: destination,
          displayFile: display.file,
          displayRank: display.rank,
          kind: 'destination',
          label: `将${selectedPiece ? pieceNames[selectedPiece.side][selectedPiece.type] : '棋子'}移动到${squareLabel(destination)}`,
        })
      }
    }
    return squares.sort(
      (left, right) =>
        right.displayRank - left.displayRank ||
        left.displayFile - right.displayFile,
    )
  }, [destinations, piecesByPosition, selected, view.perspective, view.pieces])

  const activateSquare = (square: InteractiveSquare) => {
    if (interactionLocked || !isPlayerTurn) return
    if (square.kind === 'destination' && selected) {
      onMove?.(selected, square.position)
      return
    }
    setSelected(square.position)
  }

  const boardName = replayLabel ?? `${sideNames[view.perspective]}视角中国迷雾象棋棋盘`

  return (
    <div
      className={`game-board${interactionLocked ? ' game-board--locked' : ''}`}
      aria-busy={interactionLocked}
      data-testid="game-board"
    >
      <svg
        className="game-board__svg"
        viewBox="0 0 583 583"
        role="img"
        aria-label={boardName}
      >
        <defs>
          <linearGradient id="board-paper" x1="0" y1="0" x2="1" y2="1">
            <stop offset="0" stopColor="#efdcae" />
            <stop offset="1" stopColor="#d8b775" />
          </linearGradient>
          <pattern id="fog-pattern" width="10" height="10" patternUnits="userSpaceOnUse">
            <rect width="10" height="10" fill="#172321" />
            <path d="M0 10 10 0" stroke="#273936" strokeWidth="2" />
          </pattern>
        </defs>
        <rect x="1" y="1" width="581" height="581" rx="20" className="board-frame" />
        <rect x="20" y="20" width="543" height="543" rx="11" fill="url(#board-paper)" />
        <g className="board-lines" aria-hidden="true">
          {Array.from({ length: 10 }, (_, rank) => (
            <line key={`rank-${rank}`} x1="71.5" y1={44 + rank * 55} x2="511.5" y2={44 + rank * 55} />
          ))}
          {Array.from({ length: 9 }, (_, file) => {
            const x = 71.5 + file * 55
            if (file === 0 || file === 8) {
              return <line key={`file-${file}`} x1={x} y1="44" x2={x} y2="539" />
            }
            return (
              <g key={`file-${file}`}>
                <line x1={x} y1="44" x2={x} y2="264" />
                <line x1={x} y1="319" x2={x} y2="539" />
              </g>
            )
          })}
          <path d="M236.5 44 346.5 154 M346.5 44 236.5 154 M236.5 429 346.5 539 M346.5 429 236.5 539" />
        </g>
        <g className="river-label" aria-hidden="true">
          <text x="172.5" y="300" textAnchor="middle">楚 河</text>
          <text x="410.5" y="300" textAnchor="middle">汉 界</text>
        </g>
        <g className="fog-layer" aria-hidden="true">
          {allPositions.map((position) => {
            if (visibleKeys.has(positionKey(position))) return null
            const point = boardPoint(position, view.perspective)
            return (
              <rect
                key={positionKey(position)}
                x={point.x - 26}
                y={point.y - 26}
                width="52"
                height="52"
                rx="9"
                fill="url(#fog-pattern)"
                data-testid={`fog-${positionKey(position)}`}
              />
            )
          })}
        </g>
        {selected ? (() => {
          const point = boardPoint(selected, view.perspective)
          return <circle cx={point.x} cy={point.y} r="27" className="board-selection" aria-hidden="true" />
        })() : null}
        <g className="candidate-layer" aria-hidden="true">
          {destinations.map((destination) => {
            const point = boardPoint(destination, view.perspective)
            const occupied = piecesByPosition.has(positionKey(destination))
            return occupied ? (
              <circle
                key={positionKey(destination)}
                cx={point.x}
                cy={point.y}
                r="27"
                className="candidate-capture"
              />
            ) : (
              <circle
                key={positionKey(destination)}
                cx={point.x}
                cy={point.y}
                r="8"
                className="candidate-dot"
              />
            )
          })}
        </g>
        <g className="piece-layer">
          {view.pieces.map((piece) => (
            <PieceShape
              key={`${piece.side}-${piece.type}-${positionKey(piece.position)}`}
              piece={piece}
              perspective={view.perspective}
            />
          ))}
        </g>
      </svg>
      <ul className="sr-only" aria-label="当前可见棋子">
        {view.pieces.map((piece) => (
          <li key={`accessible-${piece.side}-${piece.type}-${positionKey(piece.position)}`}>
            {sideNames[piece.side]}
            {pieceNames[piece.side][piece.type]}，位于{squareLabel(piece.position)}
          </li>
        ))}
      </ul>
      <div className="game-board__targets" aria-label="棋盘操作">
        {interactiveSquares.map((square) => {
          const point = boardPoint(square.position, view.perspective)
          const disabled = interactionLocked || !isPlayerTurn
          return (
            <button
              key={`${square.kind}-${positionKey(square.position)}`}
              type="button"
              className="board-hit-target"
              style={{ left: `${(point.x / 583) * 100}%`, top: `${(point.y / 583) * 100}%` }}
              aria-label={square.label}
              disabled={disabled}
              data-position={positionKey(square.position)}
              data-candidate={destinationKeys.has(positionKey(square.position)) || undefined}
              onClick={() => activateSquare(square)}
            />
          )
        })}
      </div>
      {interactionLocked ? <span className="sr-only">正在等待服务器确认走子</span> : null}
    </div>
  )
}
