import {
  HubConnectionBuilder,
  HttpTransportType,
  LogLevel,
  type HubConnection,
} from '@microsoft/signalr'
import { useEffect, useRef, useState } from 'react'
import type { DrawOffer, GameView, MatchFound, MatchTicket } from './types'

export type RealtimeState = 'connecting' | 'connected' | 'reconnecting' | 'disconnected'

interface LobbyHubHandlers {
  onTicket: (ticket: MatchTicket) => void
  onMatch: (match: MatchFound) => void
  onReconnect: () => void
}

interface GameHubHandlers {
  gameId: string
  version: number
  onView: (view: GameView) => void
  onDrawOffer: (offer: DrawOffer) => void
  onOpponentConnection: (connected: boolean) => void
  onReconnect: () => void
}

function buildConnection(path: string): HubConnection {
  return new HubConnectionBuilder()
    .withUrl(path, {
      transport: HttpTransportType.WebSockets | HttpTransportType.LongPolling,
      withCredentials: true,
    })
    .withAutomaticReconnect([0, 2_000, 5_000, 10_000, 20_000])
    .configureLogging(LogLevel.Warning)
    .build()
}

export function useLobbyHub(handlers: LobbyHubHandlers): RealtimeState {
  const handlersRef = useRef(handlers)
  handlersRef.current = handlers
  const [state, setState] = useState<RealtimeState>('connecting')

  useEffect(() => {
    const connection = buildConnection('/hubs/lobby')
    let disposed = false
    let retryTimer: number | undefined

    connection.on('MatchTicketUpdated', (ticket: MatchTicket) => {
      handlersRef.current.onTicket(ticket)
    })
    connection.on('MatchFound', (match: MatchFound) => {
      handlersRef.current.onMatch(match)
    })
    connection.onreconnecting(() => setState('reconnecting'))
    connection.onreconnected(() => {
      setState('connected')
      handlersRef.current.onReconnect()
    })

    const start = async () => {
      if (disposed) return
      setState('connecting')
      try {
        await connection.start()
        if (!disposed) {
          setState('connected')
          handlersRef.current.onReconnect()
        }
      } catch {
        if (!disposed) {
          setState('disconnected')
          retryTimer = window.setTimeout(start, 2_000)
        }
      }
    }

    connection.onclose(() => {
      if (!disposed) {
        setState('disconnected')
        retryTimer = window.setTimeout(start, 2_000)
      }
    })
    void start()

    return () => {
      disposed = true
      if (retryTimer !== undefined) window.clearTimeout(retryTimer)
      void connection.stop()
    }
  }, [])

  return state
}

export function useGameHub(handlers: GameHubHandlers): RealtimeState {
  const handlersRef = useRef(handlers)
  handlersRef.current = handlers
  const latestVersion = useRef(handlers.version)
  latestVersion.current = handlers.version
  const [state, setState] = useState<RealtimeState>('connecting')

  useEffect(() => {
    const gameId = encodeURIComponent(handlersRef.current.gameId)
    const connection = buildConnection(
      `/hubs/game?gameId=${gameId}&version=${latestVersion.current}`,
    )
    let disposed = false
    let retryTimer: number | undefined

    connection.on('GameViewUpdated', (view: GameView) => {
      handlersRef.current.onView(view)
    })
    connection.on('GameEnded', (view: GameView) => {
      handlersRef.current.onView(view)
    })
    connection.on('DrawOfferChanged', (offer: DrawOffer) => {
      handlersRef.current.onDrawOffer(offer)
    })
    connection.on(
      'OpponentConnectionChanged',
      (connectionState: { connected?: boolean; state?: string } | string) => {
        const connected =
          typeof connectionState === 'string'
            ? connectionState.toLowerCase() === 'connected'
            : connectionState.connected ?? connectionState.state === 'connected'
        handlersRef.current.onOpponentConnection(connected)
      },
    )
    connection.onreconnecting(() => setState('reconnecting'))
    connection.onreconnected(() => {
      setState('connected')
      handlersRef.current.onReconnect()
    })

    const start = async () => {
      if (disposed) return
      setState('connecting')
      try {
        await connection.start()
        if (!disposed) {
          setState('connected')
          handlersRef.current.onReconnect()
        }
      } catch {
        if (!disposed) {
          setState('disconnected')
          retryTimer = window.setTimeout(start, 2_000)
        }
      }
    }

    connection.onclose(() => {
      if (!disposed) {
        setState('disconnected')
        retryTimer = window.setTimeout(start, 2_000)
      }
    })
    void start()

    return () => {
      disposed = true
      if (retryTimer !== undefined) window.clearTimeout(retryTimer)
      void connection.stop()
    }
  }, [handlers.gameId])

  return state
}
