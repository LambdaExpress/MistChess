import { useQuery } from '@tanstack/react-query'
import { Navigate } from 'react-router'
import { ApiError, api, errorMessage } from '../../api/client'
import { queryKeys } from '../../api/queryKeys'
import { ErrorPanel, PageLoader } from '../../components/AppShell'

export function AdminRootPage() {
  const sessionQuery = useQuery({
    queryKey: queryKeys.adminSession,
    queryFn: api.getAdminSession,
    retry: false,
    staleTime: 30_000,
  })

  if (sessionQuery.isPending) {
    return <PageLoader label="正在检查管理员登录状态…" />
  }

  if (sessionQuery.isSuccess) {
    return <Navigate to="/admin/users" replace />
  }

  if (sessionQuery.error instanceof ApiError && sessionQuery.error.status === 401) {
    return <Navigate to="/admin/login" replace />
  }

  return (
    <ErrorPanel
      title="暂时无法检查管理员登录状态"
      detail={errorMessage(sessionQuery.error)}
      onRetry={() => void sessionQuery.refetch()}
    />
  )
}
