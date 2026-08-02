interface ReplayStepControlsProps {
  current: number
  total: number
  onPrevious: () => void
  onNext: () => void
  className?: string
}

export function ReplayStepControls({
  current,
  total,
  onPrevious,
  onNext,
  className,
}: ReplayStepControlsProps) {
  return (
    <div className={['replay-step-controls', className].filter(Boolean).join(' ')}>
      <button
        type="button"
        aria-label="上一步"
        disabled={current === 0}
        onClick={onPrevious}
      >
        上一步
      </button>
      <output
        className="replay-step-controls__count"
        aria-label={`当前第 ${current} 步，共 ${total} 步`}
      >
        {current} / {total}
      </output>
      <button
        type="button"
        aria-label="下一步"
        disabled={current === total}
        onClick={onNext}
      >
        下一步
      </button>
    </div>
  )
}
