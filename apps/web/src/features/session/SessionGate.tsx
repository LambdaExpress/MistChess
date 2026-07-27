import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef, type ReactNode } from 'react'
import { api, errorMessage } from '../../api/client'
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

export function SessionGate({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient()
  const rotationPending = useRef(false)
  const previousSessionId = useRef<string | null>(null)
  const session = useQuery({
    queryKey: queryKeys.session,
    queryFn: api.startGuestSession,
    staleTime: 60_000,
    refetchOnWindowFocus: 'always',
    retry: 2,
  })

  useEffect(() => {
    const rotateSession = async () => {
      if (rotationPending.current) return
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
