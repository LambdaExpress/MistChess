import { useQuery } from '@tanstack/react-query'
import type { ReactNode } from 'react'
import { api, errorMessage } from '../../api/client'
import { queryKeys } from '../../api/queryKeys'
import { ErrorPanel, PageLoader } from '../../components/AppShell'

export function SessionGate({ children }: { children: ReactNode }) {
  const session = useQuery({
    queryKey: queryKeys.session,
    queryFn: api.startGuestSession,
    staleTime: Number.POSITIVE_INFINITY,
    retry: 2,
  })

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
