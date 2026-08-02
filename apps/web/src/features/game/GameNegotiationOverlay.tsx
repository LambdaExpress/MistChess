import { useEffect, useRef } from 'react'
import type { GameView } from '../../api/types'
import { TakebackRequestPanel } from './TakebackRequestPanel'

interface GameNegotiationOverlayProps {
  view: GameView
  submitting: boolean
  locked: boolean
  error: string | null
  onAcceptDraw: () => void
  onRejectDraw: () => void
  onAcceptTakeback: () => void
  onRejectTakeback: () => void
}

export function GameNegotiationOverlay({
  view,
  submitting,
  locked,
  error,
  onAcceptDraw,
  onRejectDraw,
  onAcceptTakeback,
  onRejectTakeback,
}: GameNegotiationOverlayProps) {
  const responseButtonRef = useRef<HTMLButtonElement>(null)
  const drawOffer = view.drawOffer?.status === 'pending' ? view.drawOffer : null
  const takebackRequest = view.takebackRequest?.status === 'pending' ? view.takebackRequest : null
  const negotiation = drawOffer ?? takebackRequest
  const incoming = drawOffer
    ? drawOffer.offeredBy !== view.perspective
    : takebackRequest
      ? takebackRequest.requestedBy !== view.perspective
      : false

  useEffect(() => {
    if (incoming) responseButtonRef.current?.focus()
  }, [incoming, negotiation?.id, negotiation?.revision])

  if (view.status !== 'playing' || !negotiation) return null

  return (
    <div className="game-negotiation-overlay" aria-live="polite">
      <div className="game-negotiation-overlay__backdrop" aria-hidden="true" />
      <section
        className="game-negotiation-overlay__card"
        role={incoming ? 'alertdialog' : 'status'}
        aria-labelledby="game-negotiation-title"
        aria-busy={submitting}
      >
        {takebackRequest ? (
          <TakebackRequestPanel
            request={takebackRequest}
            perspective={view.perspective}
            submitting={submitting}
            locked={locked}
            error={error}
            responseButtonRef={responseButtonRef}
            onAccept={onAcceptTakeback}
            onReject={onRejectTakeback}
          />
        ) : drawOffer ? (
          <div className="draw-negotiation-panel">
            <h2 id="game-negotiation-title" className="draw-negotiation-panel__title">
              {incoming ? '对手提议和棋' : '已提议和棋'}
            </h2>
            <p className="draw-negotiation-panel__message">
              {incoming ? '接受后棋局立即结束并记为和棋。' : '已发出和棋提议，等待对手回应。'}
            </p>
            {incoming ? (
              <div className="draw-negotiation-panel__actions">
                <button
                  ref={responseButtonRef}
                  type="button"
                  className="button button--accent draw-negotiation-panel__accept"
                  disabled={locked}
                  onClick={onAcceptDraw}
                >
                  {submitting ? '正在提交…' : '同意'}
                </button>
                <button
                  type="button"
                  className="button button--secondary draw-negotiation-panel__reject"
                  disabled={locked}
                  onClick={onRejectDraw}
                >
                  拒绝
                </button>
              </div>
            ) : null}
            {error ? <p className="draw-negotiation-panel__error inline-error" role="alert">{error}</p> : null}
          </div>
        ) : null}
      </section>
    </div>
  )
}
