import { act, cleanup, render } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useGameHub, useLobbyHub } from './hubs'

const signalRMock = vi.hoisted(() => {
  type LifecycleCallback = () => void

  interface MockConnection {
    path: string
    start: () => Promise<void>
    stop: () => Promise<void>
    callbacks: {
      onclose?: LifecycleCallback
      onreconnected?: LifecycleCallback
      onreconnecting?: LifecycleCallback
    }
  }

  const connections: MockConnection[] = []

  class MockHubConnectionBuilder {
    private path = ''

    withUrl(path: string) {
      this.path = path
      return this
    }

    withAutomaticReconnect() {
      return this
    }

    configureLogging() {
      return this
    }

    build() {
      const callbacks: MockConnection['callbacks'] = {}
      const connection: MockConnection = {
        path: this.path,
        start: vi.fn<() => Promise<void>>().mockResolvedValue(undefined),
        stop: vi.fn<() => Promise<void>>().mockResolvedValue(undefined),
        callbacks,
      }

      Object.assign(connection, {
        on: vi.fn(),
        onclose: vi.fn((callback: LifecycleCallback) => {
          callbacks.onclose = callback
        }),
        onreconnected: vi.fn((callback: LifecycleCallback) => {
          callbacks.onreconnected = callback
        }),
        onreconnecting: vi.fn((callback: LifecycleCallback) => {
          callbacks.onreconnecting = callback
        }),
      })
      connections.push(connection)
      return connection
    }
  }

  return {
    connections,
    HubConnectionBuilder: MockHubConnectionBuilder,
    reset() {
      connections.length = 0
    },
  }
})

vi.mock('@microsoft/signalr', () => ({
  HubConnectionBuilder: signalRMock.HubConnectionBuilder,
  HttpTransportType: {
    WebSockets: 1,
    LongPolling: 2,
  },
  LogLevel: {
    Warning: 3,
  },
}))

const presenceListeners = new Set<EventListener>()


function LobbyHubHarness() {
  useLobbyHub({
    onTicket: vi.fn(),
    onMatch: vi.fn(),
    onReconnect: vi.fn(),
  })
  return null
}

function GameHubHarness() {
  useGameHub({
    gameId: 'game/one',
    version: 7,
    onView: vi.fn(),
    onDrawOffer: vi.fn(),
    onOpponentConnection: vi.fn(),
    onReconnect: vi.fn(),
  })
  return null
}

beforeEach(() => {
  signalRMock.reset()
})

afterEach(() => {
  cleanup()
  for (const listener of presenceListeners) {
    window.removeEventListener('mistchess:presence-refresh', listener)
  }
  presenceListeners.clear()
  vi.clearAllTimers()
  vi.useRealTimers()
  signalRMock.reset()
})

const hubCases = [
  {
    name: 'Lobby',
    path: '/hubs/lobby',
    mount: () => render(<LobbyHubHarness />),
  },
  {
    name: 'Game',
    path: '/hubs/game?gameId=game%2Fone&version=7',
    mount: () => render(<GameHubHarness />),
  },
] as const

describe.each(hubCases)('$name Hub presence refresh', ({ path, mount }) => {
  it('dispatches after start, automatic reconnection, and a delayed onclose retry', async () => {
    vi.useFakeTimers()
    const listener: EventListener = vi.fn()
    window.addEventListener('mistchess:presence-refresh', listener)
    presenceListeners.add(listener)

    mount()
    await act(async () => {
      await Promise.resolve()
    })

    expect(signalRMock.connections).toHaveLength(1)
    const connection = signalRMock.connections[0]
    expect(connection.path).toBe(path)
    expect(connection.start).toHaveBeenCalledTimes(1)
    expect(listener).toHaveBeenCalledTimes(1)

    expect(connection.callbacks.onreconnected).toBeTypeOf('function')
    act(() => connection.callbacks.onreconnected?.())

    expect(connection.start).toHaveBeenCalledTimes(1)
    expect(listener).toHaveBeenCalledTimes(2)

    expect(connection.callbacks.onclose).toBeTypeOf('function')
    act(() => connection.callbacks.onclose?.())

    await act(async () => {
      await vi.advanceTimersByTimeAsync(1_999)
    })
    expect(connection.start).toHaveBeenCalledTimes(1)
    expect(listener).toHaveBeenCalledTimes(2)

    await act(async () => {
      await vi.advanceTimersByTimeAsync(1)
    })
    expect(connection.start).toHaveBeenCalledTimes(2)
    expect(listener).toHaveBeenCalledTimes(3)
  })
})
