import type { Side } from '../../api/types'

interface PlayerClockBarProps {
  side: Side
  relationship: 'self' | 'opponent'
  totalMilliseconds: number
  turnMilliseconds: number | null
  active: boolean
  low: boolean
}

const sideNames: Record<Side, string> = { red: '红方', black: '黑方' }

function formatClock(milliseconds: number): string {
  const safeMilliseconds = Math.max(0, milliseconds)
  if (safeMilliseconds < 10_000) return (safeMilliseconds / 1_000).toFixed(1)

  const totalSeconds = Math.ceil(safeMilliseconds / 1_000)
  const minutes = Math.floor(totalSeconds / 60).toString().padStart(2, '0')
  const seconds = (totalSeconds % 60).toString().padStart(2, '0')
  return `${minutes}:${seconds}`
}

export function PlayerClockBar({
  side,
  relationship,
  totalMilliseconds,
  turnMilliseconds,
  active,
  low,
}: PlayerClockBarProps) {
  const relationshipLabel = relationship === 'self' ? '我方' : '对方'
  const classes = [
    'player-clock-bar',
    `player-clock-bar--${relationship}`,
    `player-clock-bar--${side}`,
    active ? 'player-clock-bar--active' : '',
    low ? 'player-clock-bar--low' : '',
  ].filter(Boolean).join(' ')

  return (
    <section className={classes} aria-label={`${relationshipLabel}${sideNames[side]}计时`}>
      <span className={`player-clock-bar__token side-token side-token--${side}`} aria-hidden="true">
        {side === 'red' ? '帅' : '将'}
      </span>
      <div className="player-clock-bar__identity">
        <strong>{relationshipLabel}</strong>
        <span>{sideNames[side]}</span>
      </div>
      <div className="player-clock-bar__total">
        <small>总剩余</small>
        <strong>{formatClock(totalMilliseconds)}</strong>
      </div>
      {active && turnMilliseconds !== null ? (
        <div className="player-clock-bar__turn">
          <small>本步</small>
          <span>{formatClock(turnMilliseconds)}</span>
        </div>
      ) : null}
      {low ? <em className="player-clock-bar__warning">时间不足</em> : null}
    </section>
  )
}
