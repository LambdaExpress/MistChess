import type { RefObject } from 'react'
import type { Side, TakebackRequestView } from '../../api/types'

interface TakebackRequestPanelProps {
  request: TakebackRequestView
  perspective: Side
  submitting: boolean
  locked: boolean
  error: string | null
  responseButtonRef: RefObject<HTMLButtonElement | null>
  onAccept: () => void
  onReject: () => void
}

export function TakebackRequestPanel({
  request,
  perspective,
  submitting,
  locked,
  error,
  responseButtonRef,
  onAccept,
  onReject,
}: TakebackRequestPanelProps) {
  const incoming = request.requestedBy !== perspective

  return (
    <div className="takeback-request-panel">
      <h2 id="game-negotiation-title" className="takeback-request-panel__title">
        {incoming ? '对手请求悔棋' : '已请求悔棋'}
      </h2>
      <p className="takeback-request-panel__message">
        {incoming
          ? `对手请求撤销第 ${request.requestedPly} 手。你也可以直接继续走棋。`
          : `已请求撤销第 ${request.requestedPly} 手，等待对手回应。`}
      </p>
      {incoming ? (
        <div className="takeback-request-panel__actions">
          <button
            ref={responseButtonRef}
            type="button"
            className="button button--accent takeback-request-panel__accept"
            disabled={locked}
            onClick={onAccept}
          >
            {submitting ? '正在提交…' : '同意'}
          </button>
          <button
            type="button"
            className="button button--secondary takeback-request-panel__reject"
            disabled={locked}
            onClick={onReject}
          >
            拒绝
          </button>
        </div>
      ) : null}
      {error ? <p className="takeback-request-panel__error inline-error" role="alert">{error}</p> : null}
    </div>
  )
}
