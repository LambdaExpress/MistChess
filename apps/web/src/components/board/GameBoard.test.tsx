import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import type { GameView } from '../../api/types'
import { GameBoard } from './GameBoard'

const allSquares = Array.from({ length: 90 }, (_, index) => ({
  file: index % 9,
  rank: Math.floor(index / 9),
}))

function createView(overrides: Partial<GameView> = {}): GameView {
  return {
    gameId: 'game-1',
    ruleVersion: 'fog-xiangqi-v1',
    timeControl: null,
    version: 1,
    status: 'playing',
    result: null,
    perspective: 'red',
    sideToMove: 'red',
    visibleSquares: allSquares,
    pieces: [
      { side: 'red', type: 'rook', position: { file: 0, rank: 0 } },
      { side: 'black', type: 'rook', position: { file: 4, rank: 5 } },
    ],
    candidateMoves: [
      { from: { file: 0, rank: 0 }, destinations: [{ file: 0, rank: 1 }] },
    ],
    captureSummary: { redLost: [], blackLost: [] },
    clock: null,
    drawOffer: null,
    negotiationVersion: 0,
    takebackRequest: null,
    lastAction: null,
    canRequestTakeback: false,
    ...overrides,
  }
}

describe('GameBoard', () => {
  it('removes an enemy piece when a replacement snapshot puts its square in fog', () => {
    const { rerender } = render(<GameBoard view={createView()} />)
    expect(screen.getByLabelText(/黑方车/)).toBeInTheDocument()

    rerender(
      <GameBoard
        view={createView({
          version: 2,
          visibleSquares: allSquares.filter(({ file, rank }) => file !== 4 || rank !== 5),
          pieces: [{ side: 'red', type: 'rook', position: { file: 0, rank: 0 } }],
        })}
      />,
    )

    expect(screen.queryByLabelText(/黑方车/)).not.toBeInTheDocument()
    expect(screen.getByTestId('fog-4:5')).toBeInTheDocument()
  })

  it('lists exactly the server-projected visible pieces for screen readers', () => {
    const { rerender } = render(<GameBoard view={createView()} />)
    const visiblePieces = screen.getByRole('list', { name: '当前可见棋子' })

    expect(
      within(visiblePieces).getAllByRole('listitem').map((item) => item.textContent),
    ).toEqual([
      '红方车，位于1路、1线',
      '黑方车，位于5路、6线',
    ])

    rerender(
      <GameBoard
        view={createView({
          version: 2,
          pieces: [{ side: 'red', type: 'rook', position: { file: 0, rank: 0 } }],
        })}
      />,
    )

    expect(within(visiblePieces).getAllByRole('listitem')).toHaveLength(1)
    expect(within(visiblePieces).queryByText(/黑方车/)).not.toBeInTheDocument()
  })

  it('keeps board coordinates separate from portrait piece and ring scaling', async () => {
    const user = userEvent.setup()
    const { container } = render(
      <GameBoard
        view={createView({
          candidateMoves: [
            { from: { file: 0, rank: 0 }, destinations: [{ file: 4, rank: 5 }] },
          ],
        })}
      />,
    )

    const board = screen.getByRole('img', { name: /中国迷雾象棋棋盘/ })
    expect(board).toHaveAttribute('viewBox', '0 0 583 583')
    expect(board).toHaveAttribute('preserveAspectRatio', 'none')

    const piece = screen.getByTestId('piece-0:0')
    expect(piece).toHaveAttribute('transform', 'translate(71.5 539)')
    expect(piece.firstElementChild).toHaveClass(
      'board-piece__content',
      'board-horizontal-scale',
    )

    const sourceTarget = screen.getByRole('button', { name: /选择红方车/ })
    expect(sourceTarget.style.left).toBe(`${(71.5 / 583) * 100}%`)
    expect(sourceTarget.style.top).toBe(`${(539 / 583) * 100}%`)
    await user.click(sourceTarget)

    expect(container.querySelector('.board-selection.board-horizontal-scale'))
      .toBeInTheDocument()
    expect(container.querySelector('.candidate-capture.board-horizontal-scale'))
      .toBeInTheDocument()
  })

  it('keeps the board unchanged and locks a selected move while submission is pending', async () => {
    const user = userEvent.setup()
    const onMove = vi.fn()
    const view = createView()
    const { rerender } = render(<GameBoard view={view} onMove={onMove} />)

    await user.click(screen.getByRole('button', { name: /选择红方车/ }))
    const destination = screen.getByRole('button', { name: /移动到1路、2线/ })

    rerender(<GameBoard view={view} interactionLocked onMove={onMove} />)
    expect(destination).toBeDisabled()
    await user.click(destination)

    expect(onMove).not.toHaveBeenCalled()
    expect(screen.getByTestId('piece-0:0')).toBeInTheDocument()
    expect(screen.queryByTestId('piece-0:1')).not.toBeInTheDocument()
  })

  it('highlights only an unlocked playable turn', () => {
    const { rerender } = render(<GameBoard view={createView()} />)
    const board = screen.getByTestId('game-board')

    expect(board).toHaveClass('game-board--my-turn')

    rerender(<GameBoard view={createView()} interactionLocked />)
    expect(board).not.toHaveClass('game-board--my-turn')

    rerender(<GameBoard view={createView({ sideToMove: 'black' })} />)
    expect(board).not.toHaveClass('game-board--my-turn')

    rerender(<GameBoard view={createView({ status: 'finished' })} />)
    expect(board).not.toHaveClass('game-board--my-turn')
  })

  it('uses orientation independently from the projected player perspective', () => {
    render(<GameBoard view={createView()} orientation="black" />)

    expect(screen.getByRole('img', { name: '红方视角中国迷雾象棋棋盘' }))
      .toBeInTheDocument()
    expect(screen.getByTestId('piece-0:0'))
      .toHaveAttribute('transform', 'translate(511.5 44)')
    expect(screen.getByRole('button', { name: '选择红方车，9路、10线' }))
      .toBeInTheDocument()
    expect(within(screen.getByRole('list', { name: '当前可见棋子' }))
      .getByText('红方车，位于9路、10线')).toBeInTheDocument()
  })

  it('marks red-only, black-only, and shared blind squares in omniscient view', () => {
    render(
      <GameBoard
        view={createView()}
        omniscientVisibility={{
          red: [{ file: 0, rank: 0 }, { file: 1, rank: 0 }],
          black: [{ file: 0, rank: 0 }, { file: 2, rank: 0 }],
        }}
      />,
    )

    expect(screen.queryByTestId('visibility-0:0')).not.toBeInTheDocument()
    expect(screen.getByTestId('visibility-1:0'))
      .toHaveAttribute('data-black-blind', 'true')
    expect(screen.getByTestId('visibility-1:0'))
      .not.toHaveAttribute('data-red-blind')
    expect(screen.getByTestId('visibility-1:0'))
      .toHaveAttribute('fill', 'url(#black-blind-pattern)')
    expect(screen.getByTestId('visibility-2:0'))
      .toHaveAttribute('data-red-blind', 'true')
    expect(screen.getByTestId('visibility-2:0'))
      .not.toHaveAttribute('data-black-blind')
    expect(screen.getByTestId('visibility-2:0'))
      .toHaveAttribute('fill', 'url(#red-blind-pattern)')
    expect(screen.getByTestId('visibility-3:0'))
      .toHaveAttribute('data-red-blind', 'true')
    expect(screen.getByTestId('visibility-3:0'))
      .toHaveAttribute('data-black-blind', 'true')
    expect(screen.getByTestId('visibility-3:0'))
      .toHaveAttribute('fill', 'url(#both-blind-pattern)')
    expect(screen.queryByTestId('fog-3:0')).not.toBeInTheDocument()
  })
})
