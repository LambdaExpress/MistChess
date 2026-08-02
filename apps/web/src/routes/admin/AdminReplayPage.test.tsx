import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, within } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { api } from '../../api/client'
import type { AdminReplay, ReplayProjection } from '../../api/types'
import { audioService } from '../../features/audio/audioService'
import { AdminReplayPage } from './AdminReplayPage'

const allPositions = Array.from({ length: 90 }, (_, index) => ({
  file: index % 9,
  rank: Math.floor(index / 9),
}))
const move = {
  ply: 1,
  side: 'red' as const,
  piece: 'rook' as const,
  from: { file: 0, rank: 0 },
  to: { file: 0, rank: 1 },
  captured: null,
}

function projection(
  visibleSquares: ReplayProjection['visibleSquares'],
  replayMove: ReplayProjection['move'],
): ReplayProjection {
  return {
    visibleSquares,
    pieces: [
      { side: 'red', type: 'rook', position: { file: 0, rank: 1 } },
      { side: 'black', type: 'rook', position: { file: 8, rank: 9 } },
    ],
    captureSummary: { redLost: [], blackLost: [] },
    move: replayMove,
  }
}

function replay(): AdminReplay {
  const red = projection([{ file: 0, rank: 0 }], null)
  const black = projection([{ file: 8, rank: 9 }], null)
  const omniscient = projection(allPositions, null)
  return {
    gameId: 'game-admin-1',
    ruleVersion: 'fog-xiangqi-v1',
    timeControl: '600+5',
    currentPlayerSide: null,
    red: { displayName: '红方棋手', outcome: 'win' },
    black: { displayName: '黑方棋手', outcome: 'loss' },
    result: { winner: 'red', reason: 'resignation' },
    frames: [
      {
        ply: 0,
        sideToMove: 'red',
        clock: null,
        views: { red, black, omniscient },
      },
      {
        ply: 1,
        sideToMove: 'black',
        clock: null,
        views: {
          red: projection([{ file: 0, rank: 1 }], move),
          black: projection([{ file: 8, rank: 9 }], null),
          omniscient: projection(allPositions, move),
        },
      },
      {
        ply: 1,
        sideToMove: 'black',
        clock: null,
        views: {
          red: projection([{ file: 0, rank: 1 }], null),
          black: projection([{ file: 8, rank: 9 }], null),
          omniscient: projection(allPositions, null),
        },
      },
    ],
  }
}

function renderAdminReplay() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: Infinity } },
  })
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/admin/games/game-admin-1']}>
        <Routes>
          <Route path="/admin/games/:gameId" element={<AdminReplayPage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

afterEach(() => {
  vi.restoreAllMocks()
})

describe('AdminReplayPage', () => {
  it('keeps information view, board orientation, and frame controls independent', async () => {
    vi.spyOn(api, 'getAdminReplay').mockResolvedValue(replay())
    const emitReplay = vi.spyOn(audioService, 'emitReplay').mockImplementation(() => {})

    renderAdminReplay()
    await screen.findByRole('heading', { name: '管理员棋局回放' })
    const information = screen.getByRole('group', { name: '信息视野' })
    const orientation = screen.getByRole('group', { name: '棋盘朝向' })
    const previous = screen.getByRole('button', { name: '上一步' })
    const next = screen.getByRole('button', { name: '下一步' })

    expect(within(information).getByRole('button', { name: '全局视野' }))
      .toHaveAttribute('aria-pressed', 'true')
    expect(within(orientation).getByRole('button', { name: '红方在下' }))
      .toHaveAttribute('aria-pressed', 'true')
    expect(screen.getByRole('img', {
      name: '全局视野，红方在下，第 0 个半回合',
    })).toBeInTheDocument()
    expect(screen.getByTestId('visibility-0:0')).toHaveAttribute(
      'data-black-blind',
      'true',
    )
    expect(screen.getByTestId('visibility-8:9')).toHaveAttribute(
      'data-red-blind',
      'true',
    )
    expect(screen.getByTestId('visibility-4:4')).toHaveAttribute(
      'data-red-blind',
      'true',
    )
    expect(screen.getByTestId('visibility-4:4')).toHaveAttribute(
      'data-black-blind',
      'true',
    )

    fireEvent.click(within(orientation).getByRole('button', { name: '黑方在下' }))
    expect(within(information).getByRole('button', { name: '全局视野' }))
      .toHaveAttribute('aria-pressed', 'true')
    fireEvent.click(within(information).getByRole('button', { name: '红方视野' }))
    expect(within(orientation).getByRole('button', { name: '黑方在下' }))
      .toHaveAttribute('aria-pressed', 'true')
    expect(screen.getByRole('img', {
      name: '红方视野，黑方在下，第 0 个半回合',
    })).toBeInTheDocument()
    expect(screen.queryByTestId('visibility-4:4')).not.toBeInTheDocument()
    expect(screen.getByTestId('fog-4:4')).toBeInTheDocument()

    expect(screen.getByLabelText('当前第 0 步，共 2 步')).toHaveTextContent('0 / 2')
    expect(previous).toBeDisabled()
    expect(next).toBeEnabled()
    expect(screen.queryByRole('slider')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /跳到/ })).not.toBeInTheDocument()

    fireEvent.click(next)
    expect(screen.getByLabelText('当前第 1 步，共 2 步')).toHaveTextContent('1 / 2')
    expect(emitReplay).toHaveBeenCalledWith(expect.any(String), 1, ['move-opponent'])
    emitReplay.mockClear()
    fireEvent.click(previous)
    expect(emitReplay).not.toHaveBeenCalled()
  })
})
