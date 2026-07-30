import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef, useState, type ReactNode } from 'react'
import { ApiError, api, errorMessage } from '../../api/client'
import { queryKeys } from '../../api/queryKeys'
import { ErrorPanel, PageLoader } from '../../components/AppShell'

const PROTECTED_QUERY_ROOTS: Record<string, true> = {
  session: true,
  matchmaking: true,
  room: true,
  game: true,
  history: true,
  'private-replay': true,
}

const HEARTBEAT_INTERVAL_MS = 30_000
const DEFAULT_BAN_REASON = '此游客账号已被管理员封禁。'

function accountBanReason(error: unknown): string | null {
  if (!(error instanceof ApiError) || error.code !== 'PLAYER_BANNED') return null
  return typeof error.problem.detail === 'string' && error.problem.detail.trim()
    ? error.problem.detail
    : DEFAULT_BAN_REASON
}

export function SessionGate({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient()
  const rotationPending = useRef(false)
  const previousSessionId = useRef<string | null>(null)
  const heartbeatSessionId = useRef<string | null>(null)
  const bannedRef = useRef(false)
  const [realtimeBanReason, setRealtimeBanReason] = useState<string | null>(null)
  const session = useQuery({
    queryKey: queryKeys.session,
    queryFn: api.startGuestSession,
    enabled: realtimeBanReason === null,
    staleTime: 60_000,
    refetchOnWindowFocus: (query) =>
      accountBanReason(query.state.error) === null ? 'always' : false,
    retry: (failureCount, error) =>
      accountBanReason(error) === null && failureCount < 2,
  })
  const bannedReason = realtimeBanReason ?? accountBanReason(session.error)
  bannedRef.current = bannedReason !== null

  useEffect(() => {
    const rotateSession = async () => {
      if (rotationPending.current || bannedRef.current) return
      rotationPending.current = true
      const isProtected = (query: { queryKey: readonly unknown[] }) =>
        PROTECTED_QUERY_ROOTS[String(query.queryKey[0])] === true
      try {
        await queryClient.cancelQueries({ predicate: isProtected })
        queryClient.removeQueries({ predicate: isProtected })
        await queryClient.fetchQuery({
          queryKey: queryKeys.session,
          queryFn: api.startGuestSession,
          staleTime: 60_000,
        })
      } finally {
        rotationPending.current = false
      }
    }
    window.addEventListener('mistchess:session-invalid', rotateSession)
    return () => window.removeEventListener('mistchess:session-invalid', rotateSession)
  }, [queryClient])

  useEffect(() => {
    const handleAccountBanned = (event: Event) => {
      const detail = (event as CustomEvent<string>).detail
      bannedRef.current = true
      setRealtimeBanReason(
        typeof detail === 'string' && detail.trim() ? detail : DEFAULT_BAN_REASON,
      )
      void queryClient.cancelQueries({
        predicate: (query) =>
          PROTECTED_QUERY_ROOTS[String(query.queryKey[0])] === true,
      })
    }
    window.addEventListener('mistchess:account-banned', handleAccountBanned)
    return () =>
      window.removeEventListener('mistchess:account-banned', handleAccountBanned)
  }, [queryClient])

  useEffect(() => {
    const currentSessionId = session.data?.playerId
    if (!currentSessionId || bannedReason) return

    const heartbeat = () => {
      void api.heartbeatSession().catch(() => {
        // The request layer owns session-invalid and account-banned transitions.
      })
    }
    if (heartbeatSessionId.current !== currentSessionId) {
      heartbeatSessionId.current = currentSessionId
      heartbeat()
    }

    const interval = window.setInterval(heartbeat, HEARTBEAT_INTERVAL_MS)
    const heartbeatWhenVisible = () => {
      if (document.visibilityState === 'visible') heartbeat()
    }
    document.addEventListener('visibilitychange', heartbeatWhenVisible)
    window.addEventListener('online', heartbeat)
    window.addEventListener('mistchess:presence-refresh', heartbeat)
    return () => {
      window.clearInterval(interval)
      document.removeEventListener('visibilitychange', heartbeatWhenVisible)
      window.removeEventListener('online', heartbeat)
      window.removeEventListener('mistchess:presence-refresh', heartbeat)
    }
  }, [bannedReason, session.data?.playerId])

  useEffect(() => {
    const currentSessionId = session.data?.playerId
    if (!currentSessionId) return
    const previous = previousSessionId.current
    previousSessionId.current = currentSessionId
    if (!previous || previous === currentSessionId) return

    void queryClient.cancelQueries({
      predicate: (query) =>
        ['history', 'private-replay', 'game', 'room', 'matchmaking']
          .includes(String(query.queryKey[0])),
    }).then(() => {
      queryClient.removeQueries({
        predicate: (query) =>
          ['history', 'private-replay', 'game', 'room', 'matchmaking']
            .includes(String(query.queryKey[0])),
      })
    })
  }, [queryClient, session.data?.playerId])

  if (bannedReason) {
    return (
      <main className="app-main app-main--centered">
        <ErrorPanel title="账号已被封禁" detail={bannedReason} />
      </main>
    )
  }
  if (session.isPending) return <PageLoader />
  if (session.isError) {
    return (
      <main className="app-main app-main--centered">
        <ErrorPanel
          title="暂时无法进入迷雾"
          detail={errorMessage(session.error)}
          onRetry={() => void session.refetch()}
        />
      </main>
    )
  }

  return children
}
