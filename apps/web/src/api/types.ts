import type { components } from './schema'

export type Side = components['schemas']['Side']
export type PieceType = components['schemas']['PieceType']
export type Position = components['schemas']['Position']
export type GuestSession = components['schemas']['GuestSessionView']
export type GameOptions = components['schemas']['GameOptionsView']
export type RoomView = components['schemas']['RoomView']
export type MatchTicket = components['schemas']['MatchTicketView']
export type MatchFound = {
  ticketId: MatchTicket['ticketId']
  gameId: NonNullable<MatchTicket['gameId']>
  perspective: Side
}
export type GameView = components['schemas']['GameView']
export type PieceView = components['schemas']['PieceView']
export type CandidateMove = components['schemas']['CandidateMoveView']
export type DrawOffer = components['schemas']['DrawOfferView']
export type HistoricalGame = components['schemas']['HistoricalGameSummaryView']
export type HistoricalGamesPage = components['schemas']['HistoricalGamesPageView']
export type HistoricalReplay = components['schemas']['HistoricalReplayView']
export type HistoricalReplayFrame = components['schemas']['HistoricalReplayFrameView']
export type ReplayProjection = components['schemas']['ReplayFrameProjectionView']
export type ReplayShareCreated = components['schemas']['ReplayShareCreatedView']
export type MoveRequest = components['schemas']['MoveRequest']
export type GameResult = components['schemas']['GameResultView']

export const RULE_VERSION = 'fog-xiangqi-v1'
export const QUICK_MATCH_CLIENT_REQUEST_ID_KEY = 'mistchess.quickMatch.clientRequestId'

export function createClientId(): string {
  if (typeof crypto.randomUUID === 'function') return crypto.randomUUID()

  const bytes = crypto.getRandomValues(new Uint8Array(16))
  bytes[6] = (bytes[6] & 0x0f) | 0x40
  bytes[8] = (bytes[8] & 0x3f) | 0x80
  const hex = Array.from(
    bytes,
    (byte) => byte.toString(16).padStart(2, '0'),
  ).join('')
  return [
    hex.slice(0, 8),
    hex.slice(8, 12),
    hex.slice(12, 16),
    hex.slice(16, 20),
    hex.slice(20),
  ].join('-')
}
