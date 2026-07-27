import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { useNavigate } from 'react-router'
import { ApiError, api, errorMessage } from '../api/client'
import { queryKeys } from '../api/queryKeys'
import {
  QUICK_MATCH_CLIENT_REQUEST_ID_KEY,
  createClientId,
  RULE_VERSION,
  type GuestSession,
  type MatchTicket,
} from '../api/types'


export function HomePage() {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [roomCode, setRoomCode] = useState('')
  const [roomTimeControl, setRoomTimeControl] = useState('')
  const sessionQuery = useQuery<GuestSession>({
    queryKey: queryKeys.session,
    queryFn: api.startGuestSession,
    staleTime: Number.POSITIVE_INFINITY,
  })
  const optionsQuery = useQuery({
    queryKey: queryKeys.gameOptions,
    queryFn: api.getGameOptions,
    staleTime: Number.POSITIVE_INFINITY,
  })

  const enterMatch = (ticket: MatchTicket) => {
    queryClient.setQueryData(queryKeys.currentTicket, ticket)
    sessionStorage.removeItem(QUICK_MATCH_CLIENT_REQUEST_ID_KEY)
    void navigate('/match')
  }

  const createRoom = useMutation({
    mutationFn: api.createRoom,
    onSuccess: (room) => {
      queryClient.setQueryData(queryKeys.room(room.code), room)
      void navigate(`/room/${encodeURIComponent(room.code)}`)
    },
  })
  const joinRoom = useMutation({
    mutationFn: api.joinRoom,
    onSuccess: (room) => {
      queryClient.setQueryData(queryKeys.room(room.code), room)
      void navigate(`/room/${encodeURIComponent(room.code)}`)
    },
  })
  const startMatch = useMutation({
    mutationFn: api.createMatchTicket,
    onSuccess: enterMatch,
    onError: async (error) => {
      if (!(error instanceof ApiError) || error.code !== 'ACTIVE_TICKET_EXISTS') return

      try {
        enterMatch(await api.getCurrentMatchTicket())
      } catch {
        // Keep the request ID so the user can retry after a transient recovery failure.
      }
    },
  })

  const submitRoomCode = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    const normalizedCode = roomCode.trim().toUpperCase()
    if (normalizedCode) joinRoom.mutate(normalizedCode)
  }

  const startQuickMatch = () => {
    let clientRequestId = sessionStorage.getItem(QUICK_MATCH_CLIENT_REQUEST_ID_KEY)
    if (!clientRequestId) {
      clientRequestId = createClientId()
      sessionStorage.setItem(QUICK_MATCH_CLIENT_REQUEST_ID_KEY, clientRequestId)
    }
    startMatch.mutate(clientRequestId)
  }

  const options = optionsQuery.data
  const selectedRoomTimeControl = roomTimeControl ||
    options?.defaultRoomTimeControlId ||
    ''
  const roomTimeControlRequest = selectedRoomTimeControl === 'untimed'
    ? null
    : selectedRoomTimeControl
  const activeError = createRoom.error ?? joinRoom.error ?? startMatch.error
    ?? optionsQuery.error
  const busy = createRoom.isPending || joinRoom.isPending || startMatch.isPending

  return (
    <div className="home-page">
      <section className="home-hero">
        <div className="home-hero__eyebrow">看得见的，未必是全部</div>
        <h1>
          在迷雾中
          <span>走出胜局</span>
        </h1>
        <p>
          标准中国象棋规则，叠加双方独立动态视野。每一步都由服务器裁定，
          看清局部，判断全局。
        </p>
        <div className="feature-row" aria-label="游戏特性">
          <span>双人实时</span>
          <span>无需注册</span>
          <span>完整回放</span>
        </div>
      </section>

      <section className="entry-grid" aria-label="开始游戏">
        <article className="entry-card entry-card--primary">
          <div className="entry-card__number" aria-hidden="true">01</div>
          <p className="entry-card__kicker">QUICK MATCH</p>
          <h2>快速匹配</h2>
          <p>固定 10 分钟基础时间，每次合法非终局走子增加 5 秒；系统优先安排实力接近的对手。</p>
          <div className="rating-summary">
            <span>当前匹配分</span>
            <strong>
              {sessionQuery.data?.rating.gamesPlayed
                ? sessionQuery.data.rating.rating
                : `暂定 ${sessionQuery.data?.rating.rating ?? 1500}`}
            </strong>
            <small>分数会随已完成的快速匹配对局调整</small>
          </div>
          <button
            type="button"
            className="button button--accent button--wide"
            disabled={busy || !options}
            onClick={startQuickMatch}
          >
            {startMatch.isPending ? '正在入池…' : '寻找对手'}
          </button>
          <small>{RULE_VERSION} · {options?.quickMatchTimeControl.label ?? '10 分钟 + 5 秒'}</small>
        </article>

        <article className="entry-card">
          <div className="entry-card__number" aria-hidden="true">02</div>
          <p className="entry-card__kicker">PRIVATE ROOM</p>
          <h2>好友房间</h2>
          <p>创建专属房间，把房间码发给好友，双方准备后开局。</p>
          <label className="room-time-control" htmlFor="room-time-control">
            <span>计时模式</span>
            <select
              id="room-time-control"
              value={selectedRoomTimeControl}
              disabled={busy || !options}
              onChange={(event) => setRoomTimeControl(event.target.value)}
            >
              {options?.roomTimeControls.map((option) => (
                <option key={option.id} value={option.id}>{option.label}</option>
              ))}
              {options?.allowUntimedRooms ? <option value="untimed">无计时</option> : null}
            </select>
          </label>
          <button
            type="button"
            className="button button--secondary button--wide"
            disabled={busy || !options}
            onClick={() => createRoom.mutate(roomTimeControlRequest)}
          >
            {createRoom.isPending ? '正在创建…' : '创建房间'}
          </button>
          <div className="divider"><span>或使用房间码</span></div>
          <form className="room-code-form" onSubmit={submitRoomCode}>
            <label htmlFor="room-code">八位房间码</label>
            <div>
              <input
                id="room-code"
                value={roomCode}
                onChange={(event) => setRoomCode(event.target.value.toUpperCase())}
                inputMode="text"
                autoComplete="off"
                maxLength={8}
                placeholder="例如 MIST8822"
                aria-describedby={joinRoom.isError ? 'home-error' : undefined}
              />
              <button
                type="submit"
                className="button button--compact"
                disabled={busy || roomCode.trim().length === 0}
              >
                加入
              </button>
            </div>
          </form>
        </article>
      </section>

      {activeError ? (
        <p id="home-error" className="inline-error" role="alert">
          {errorMessage(activeError)}
        </p>
      ) : null}

      <section className="rules-strip" aria-label="迷雾规则摘要">
        <div><strong>01</strong><span>己方棋子与行动路线提供视野</span></div>
        <div><strong>02</strong><span>每步后视野重算，不保留幽灵棋子</span></div>
        <div><strong>03</strong><span>只按服务器候选落点行动</span></div>
      </section>
    </div>
  )
}
