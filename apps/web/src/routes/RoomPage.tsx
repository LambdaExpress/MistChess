import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { useNavigate, useParams } from 'react-router'
import { api, errorMessage } from '../api/client'
import { queryKeys } from '../api/queryKeys'
import type { Side } from '../api/types'
import { ErrorPanel, PageLoader } from '../components/AppShell'

const sideLabel: Record<Side, string> = { red: '红方', black: '黑方' }

export function RoomPage() {
  const params = useParams<{ code: string }>()
  const code = (params.code ?? '').trim().toUpperCase()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [copied, setCopied] = useState(false)

  const roomQuery = useQuery({
    queryKey: queryKeys.room(code),
    queryFn: () => api.joinRoom(code),
    enabled: code.length > 0,
    refetchInterval: (query) => (query.state.data?.gameId ? false : 5_000),
    retry: 1,
  })
  const room = roomQuery.data

  useEffect(() => {
    if (room?.gameId) {
      void navigate(`/game/${encodeURIComponent(room.gameId)}`, { replace: true })
    }
  }, [navigate, room?.gameId])

  const ready = useMutation({
    mutationFn: (nextReady: boolean) => api.setRoomReady(code, nextReady),
    onSuccess: (updatedRoom) => {
      queryClient.setQueryData(queryKeys.room(code), updatedRoom)
      if (updatedRoom.gameId) {
        void navigate(`/game/${encodeURIComponent(updatedRoom.gameId)}`, { replace: true })
      }
    },
  })

  const leave = useMutation({
    mutationFn: () => api.leaveRoom(code),
    onSuccess: () => {
      queryClient.removeQueries({ queryKey: queryKeys.room(code) })
      void navigate('/', { replace: true })
    },
  })

  if (!code) {
    return <ErrorPanel title="房间码无效" detail="请从首页输入有效房间码。" />
  }
  if (roomQuery.isPending) return <PageLoader label="正在进入好友房间…" />
  if (roomQuery.isError || !room) {
    return (
      <ErrorPanel
        title="无法进入房间"
        detail={errorMessage(roomQuery.error)}
        onRetry={() => void roomQuery.refetch()}
      />
    )
  }

  const currentPlayer = room.players.find((player) => player.isCurrentPlayer)
  const waitingForOpponent = room.players.length < 2
  const copyCode = async () => {
    await navigator.clipboard.writeText(room.code)
    setCopied(true)
    window.setTimeout(() => setCopied(false), 2_000)
  }

  return (
    <div className="room-page">
      <section className="room-heading">
        <p className="page-kicker">PRIVATE ROOM</p>
        <h1>好友对局房间</h1>
        <p>{waitingForOpponent ? '把房间码发给好友，等待对方加入。' : '双方都准备后，对局将自动开始。'}</p>
      </section>

      <section className="room-code-card" aria-labelledby="room-code-title">
        <span id="room-code-title">房间码</span>
        <strong aria-label={`房间码 ${room.code}`}>{room.code}</strong>
        <button type="button" className="copy-button" onClick={() => void copyCode()}>
          {copied ? '已复制' : '复制房间码'}
        </button>
      </section>

      <section className="player-slots" aria-label="房间玩家">
        {[0, 1].map((index) => {
          const player = room.players[index]
          return (
            <article className={`player-slot${player ? ' player-slot--occupied' : ''}`} key={index}>
              <div className="player-slot__piece" aria-hidden="true">
                {player?.side === 'black' ? '将' : player ? '帅' : '？'}
              </div>
              {player ? (
                <>
                  <div>
                    <p>{player.displayName}{player.isCurrentPlayer ? '（你）' : ''}</p>
                    <span>{player.side ? sideLabel[player.side] : '开局时随机分方'}</span>
                  </div>
                  <strong className={player.isReady ? 'ready-state ready-state--yes' : 'ready-state'}>
                    {player.isReady ? '已准备' : '未准备'}
                  </strong>
                </>
              ) : (
                <div>
                  <p>等待玩家加入</p>
                  <span>空位</span>
                </div>
              )}
            </article>
          )
        })}
      </section>

      <section className="room-actions">
        <button
          type="button"
          className={`button button--wide ${currentPlayer?.isReady ? 'button--secondary' : 'button--accent'}`}
          disabled={ready.isPending || leave.isPending}
          onClick={() => ready.mutate(!(currentPlayer?.isReady ?? false))}
        >
          {ready.isPending ? '正在同步…' : currentPlayer?.isReady ? '取消准备' : '我已准备'}
        </button>
        <p>准备状态会自动同步；开局后页面将进入你的独立迷雾视角。</p>
        <button
          type="button"
          className="text-link"
          disabled={leave.isPending || ready.isPending}
          onClick={() => leave.mutate()}
        >
          {leave.isPending ? '正在离开…' : '离开房间并返回首页'}
        </button>
        {ready.isError || leave.isError ? (
          <p className="inline-error" role="alert">{errorMessage(ready.error ?? leave.error)}</p>
        ) : null}
      </section>
    </div>
  )
}
