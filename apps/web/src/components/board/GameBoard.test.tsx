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
})
