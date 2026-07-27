import type { Side } from '../../api/types'

export function interpolateClock(
  redMilliseconds: number,
  blackMilliseconds: number,
  sideToMove: Side,
  playing: boolean,
  elapsedMilliseconds: number,
) {
  const elapsed = playing ? Math.max(0, elapsedMilliseconds) : 0
  return {
    redMilliseconds: Math.max(
      0,
      redMilliseconds - (sideToMove === 'red' ? elapsed : 0),
    ),
    blackMilliseconds: Math.max(
      0,
      blackMilliseconds - (sideToMove === 'black' ? elapsed : 0),
    ),
  }
}
