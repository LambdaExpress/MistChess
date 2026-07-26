import type { Position, Side } from '../../api/types'

export interface DisplayPosition {
  file: number
  rank: number
}

export function toDisplayPosition(
  position: Position,
  perspective: Side,
): DisplayPosition {
  if (perspective === 'red') return position
  return { file: 8 - position.file, rank: 9 - position.rank }
}

export function positionKey(position: Position): string {
  return `${position.file}:${position.rank}`
}

export function samePosition(left: Position, right: Position): boolean {
  return left.file === right.file && left.rank === right.rank
}
