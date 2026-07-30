import { useQuery } from '@tanstack/react-query'
import { Navigate, Outlet } from 'react-router'
import { ApiError, api, errorMessage } from '../../api/client'
import { queryKeys } from '../../api/queryKeys'
import { ErrorPanel, PageLoader } from '../../components/AppShell'

export function AdminGate() {
  const sessionQuery = useQuery({
    queryKey: queryKeys.adminSession,
    queryFn: api.getAdminSession,
    retry: false,
    staleTime: 30_000,
    refetchOnWindowFocus: 'always',
  })

  if (sessionQuery.isPending) {
    return <PageLoader label="正在验证管理员会话…" />
  }

  if (sessionQuery.isError) {
    if (sessionQuery.error instanceof ApiError && sessionQuery.error.status === 401) {
      return (
        <Navigate
          to="/admin/login"
          replace
          state={{ adminSessionExpired: true }}
        />
      )
    }

    return (
      <ErrorPanel
        title="暂时无法验证管理员会话"
        detail={errorMessage(sessionQuery.error)}
        onRetry={() => void sessionQuery.refetch()}
      />
    )
  }

  return <Outlet />
}
