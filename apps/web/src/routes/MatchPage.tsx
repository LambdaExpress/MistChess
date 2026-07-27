import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { api, errorMessage, matchCreatedGameId } from '../api/client'
import { useLobbyHub, type RealtimeState } from '../api/hubs'
import { queryKeys } from '../api/queryKeys'
import { createClientId } from '../api/types'
import { ErrorPanel, PageLoader } from '../components/AppShell'
import { audioService } from '../features/audio/audioService'

const realtimeLabels: Record<RealtimeState, string> = {
  connecting: '正在连接大厅',
  connected: '大厅已连接',
  reconnecting: '正在重连大厅',
  disconnected: '大厅连接中断，仍将通过 HTTP 恢复',
}

export function MatchPage() {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [now, setNow] = useState(() => Date.now())

  const ticketQuery = useQuery({
    queryKey: queryKeys.currentTicket,
    queryFn: api.getCurrentMatchTicket,
    refetchInterval: 15_000,
    retry: 1,
  })

  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), 1_000)
    return () => window.clearInterval(timer)
  }, [])

  const realtimeState = useLobbyHub({
    onTicket: (ticket) => {
      queryClient.setQueryData(queryKeys.currentTicket, ticket)
      if (ticket.status === 'matched' && ticket.gameId) {
        audioService.emit(ticket.gameId, 0, 'match-found')
        void navigate(`/game/${encodeURIComponent(ticket.gameId)}`)
      }
    },
    onMatch: (match) => {
      audioService.emit(match.gameId, 0, 'match-found')
      void navigate(`/game/${encodeURIComponent(match.gameId)}`)
    },
    onReconnect: () => {
      void ticketQuery.refetch()
    },
  })

  const ticket = ticketQuery.data
  useEffect(() => {
    if (ticket?.status === 'matched' && ticket.gameId) {
      void navigate(`/game/${encodeURIComponent(ticket.gameId)}`, { replace: true })
    }
  }, [navigate, ticket?.gameId, ticket?.status])

  const heartbeat = useMutation({
    mutationFn: api.heartbeatMatchTicket,
    onSuccess: (updatedTicket) => {
      queryClient.setQueryData(queryKeys.currentTicket, updatedTicket)
    },
    onError: (error) => {
      const gameId = matchCreatedGameId(error)
      if (gameId) void navigate(`/game/${encodeURIComponent(gameId)}`, { replace: true })
      else void ticketQuery.refetch()
    },
  })

  const heartbeatTicketId = ticket?.status === 'searching' ? ticket.ticketId : undefined
  const sendHeartbeat = heartbeat.mutate
  useEffect(() => {
    if (!heartbeatTicketId) return
    const timer = window.setInterval(() => sendHeartbeat(heartbeatTicketId), 30_000)
    return () => window.clearInterval(timer)
  }, [heartbeatTicketId, sendHeartbeat])

  const cancel = useMutation({
    mutationFn: api.cancelMatchTicket,
    onSuccess: (updatedTicket) => {
      queryClient.setQueryData(queryKeys.currentTicket, updatedTicket)
      if (updatedTicket.status === 'matched' && updatedTicket.gameId) {
        void navigate(`/game/${encodeURIComponent(updatedTicket.gameId)}`, { replace: true })
      } else {
        void navigate('/', { replace: true })
      }
    },
    onError: (error) => {
      const gameId = matchCreatedGameId(error)
      if (gameId) {
        void navigate(`/game/${encodeURIComponent(gameId)}`, { replace: true })
      }
    },
  })

  const restart = useMutation({
    mutationFn: api.createMatchTicket,
    onSuccess: (newTicket) => {
      queryClient.setQueryData(queryKeys.currentTicket, newTicket)
    },
  })

  if (ticketQuery.isPending) return <PageLoader label="正在恢复匹配票据…" />
  if (ticketQuery.isError && !ticket) {
    return (
      <ErrorPanel
        title="没有可恢复的匹配"
        detail={errorMessage(ticketQuery.error)}
        onRetry={() => void ticketQuery.refetch()}
      />
    )
  }
  if (!ticket) return null

  const elapsedSeconds = Math.max(
    0,
    Math.floor((now - new Date(ticket.createdAt).getTime()) / 1_000),
  )
  const minutes = Math.floor(elapsedSeconds / 60).toString().padStart(2, '0')
  const seconds = (elapsedSeconds % 60).toString().padStart(2, '0')
  const isSearching = ticket.status === 'searching'
  const actionError = cancel.error ?? heartbeat.error ?? restart.error

  return (
    <div className="match-page">
      <section className="match-orbit" aria-labelledby="match-title">
        <div className={`radar${isSearching ? ' radar--active' : ''}`} aria-hidden="true">
          <span className="radar__ring radar__ring--one" />
          <span className="radar__ring radar__ring--two" />
          <span className="radar__sweep" />
          <span className="radar__piece">帅</span>
        </div>
        <p className="page-kicker">MATCHMAKING</p>
        <h1 id="match-title">
          {isSearching ? '正在为你匹配对手…' : ticket.status === 'expired' ? '匹配票据已过期' : '匹配状态已更新'}
        </h1>
        <p className="match-subtitle">
          {isSearching
            ? '请保持页面开启。即使大厅短暂断开，心跳与状态查询仍会继续。'
            : '可以返回首页，或创建一张新的快速匹配票据。'}
        </p>
        <div className="match-timer" aria-label={`已等待 ${minutes} 分 ${seconds} 秒`}>
          <span>{minutes}</span><i>:</i><span>{seconds}</span>
        </div>
        <div className="connection-pill" data-state={realtimeState}>
          <span aria-hidden="true" />{realtimeLabels[realtimeState]}
        </div>
      </section>

      <section className="match-detail-card" aria-label="匹配详情">
        <dl>
          <div><dt>规则版本</dt><dd>{ticket.ruleVersion}</dd></div>
          <div><dt>计时配置</dt><dd>{ticket.timeControl ?? '无计时'}</dd></div>
          <div><dt>单步上限</dt><dd>{ticket.moveTimeLimitSeconds ? `${ticket.moveTimeLimitSeconds} 秒` : '不限制'}</dd></div>
          <div><dt>票据状态</dt><dd>{ticket.status}</dd></div>
          <div><dt>票据编号</dt><dd className="mono">{ticket.ticketId.slice(-8)}</dd></div>
        </dl>
        {isSearching ? (
          <button
            type="button"
            className="button button--danger-quiet button--wide"
            disabled={cancel.isPending}
            onClick={() => cancel.mutate(ticket.ticketId)}
          >
            {cancel.isPending ? '正在确认配对状态…' : '取消匹配'}
          </button>
        ) : ticket.status === 'expired' || ticket.status === 'cancelled' ? (
          <button
            type="button"
            className="button button--accent button--wide"
            disabled={restart.isPending}
            onClick={() => restart.mutate(createClientId())}
          >
            {restart.isPending ? '正在重新入池…' : '重新寻找对手'}
          </button>
        ) : null}
        <Link to="/" className="text-link">返回首页</Link>
        {actionError && !matchCreatedGameId(actionError) ? (
          <p className="inline-error" role="alert">{errorMessage(actionError)}</p>
        ) : null}
      </section>
    </div>
  )
}
