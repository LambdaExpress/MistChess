import type { components } from './schema'

export type Side = components['schemas']['Side']
export type PieceType = components['schemas']['PieceType']
export type Position = components['schemas']['Position']
export type GuestSession = components['schemas']['GuestSessionView']
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
export type ReplayView = components['schemas']['ReplayView']
export type ReplayFrame = components['schemas']['ReplayFrameView']
export type MoveRequest = components['schemas']['MoveRequest']
export type GameResult = components['schemas']['GameResultView']

export const RULE_VERSION = 'fog-xiangqi-v1'
