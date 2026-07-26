import { describe, expect, it } from 'vitest'
import { toDisplayPosition } from './coordinates'

describe('toDisplayPosition', () => {
  it('keeps logical coordinates for the red perspective', () => {
    expect(toDisplayPosition({ file: 2, rank: 7 }, 'red')).toEqual({ file: 2, rank: 7 })
  })

  it('rotates both axes for the black perspective', () => {
    expect(toDisplayPosition({ file: 0, rank: 0 }, 'black')).toEqual({ file: 8, rank: 9 })
    expect(toDisplayPosition({ file: 8, rank: 9 }, 'black')).toEqual({ file: 0, rank: 0 })
    expect(toDisplayPosition({ file: 3, rank: 6 }, 'black')).toEqual({ file: 5, rank: 3 })
  })
})
