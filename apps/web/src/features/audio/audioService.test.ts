import { beforeEach, describe, expect, it, vi } from 'vitest'
import { AudioService } from './audioService'

class FakeAudio extends EventTarget {
  static instances: FakeAudio[] = []
  static rejectPlayback = false
  static supportsOgg = true
  static playbackEvents: string[] = []

  readonly dataset: Record<string, string> = {}
  readonly src: string
  currentTime = 0
  playbackRate = 1
  preload = ''
  volume = 1
  readonly pause = vi.fn()
  readonly play = vi.fn(() => {
    if (FakeAudio.rejectPlayback) return Promise.reject(new Error('playback failed'))
    const event = this.dataset.soundEvent ?? 'probe'
    FakeAudio.playbackEvents.push(`play:${event}`)
    queueMicrotask(() => {
      FakeAudio.playbackEvents.push(`ended:${event}`)
      this.dispatchEvent(new Event('ended'))
    })
    return Promise.resolve()
  })

  constructor(src = '') {
    super()
    this.src = src
    FakeAudio.instances.push(this)
  }

  canPlayType(type: string) {
    return type.startsWith('audio/ogg') && FakeAudio.supportsOgg ? 'probably' : ''
  }
}

async function flushTransitions() {
  for (let index = 0; index < 12; index += 1) await Promise.resolve()
}

beforeEach(() => {
  localStorage.clear()
  FakeAudio.instances = []
  FakeAudio.playbackEvents = []
  FakeAudio.rejectPlayback = false
  FakeAudio.supportsOgg = true
  vi.stubGlobal('Audio', FakeAudio)
})

describe('AudioService', () => {
  it('plays one queued important event after the first user unlock', async () => {
    const service = new AudioService()

    service.emit('game-1', 4, 'match-found')
    await flushTransitions()
    expect(FakeAudio.instances).toHaveLength(0)

    await service.unlock()
    expect(FakeAudio.instances).toHaveLength(3)
    const unlockProbe = FakeAudio.instances.find(
      (player) => player.dataset.soundEvent === 'game-start',
    )
    const queuedPlayer = FakeAudio.instances.find(
      (player) => player.dataset.soundEvent === 'match-found',
    )
    expect(unlockProbe?.volume).toBe(0)
    expect(unlockProbe?.pause).toHaveBeenCalledOnce()
    expect(queuedPlayer?.play).toHaveBeenCalledOnce()

    service.emit('game-1', 4, 'match-found')
    await flushTransitions()
    expect(FakeAudio.instances).toHaveLength(3)
  })

  it('maps move and capture events to distinct Ogg assets at normal playback rate', async () => {
    const service = new AudioService()
    await service.unlock()

    service.emit('game-assets', 1, 'move-self')
    service.emit('game-assets', 2, 'move-opponent')
    service.emit('game-assets', 3, 'capture')
    await flushTransitions()

    const played = FakeAudio.instances.filter((player) => player.play.mock.calls.length > 0)
    expect(played.slice(-3).map((player) => player.src)).toEqual([
      '/audio/move.ogg',
      '/audio/move.ogg',
      '/audio/capture-chi.ogg',
    ])
    expect(played.slice(-3).map((player) => player.playbackRate)).toEqual([1, 1, 1])
  })

  it('falls back to MP3 when Ogg Vorbis is unavailable', async () => {
    FakeAudio.supportsOgg = false
    const service = new AudioService()
    await service.unlock()

    service.emit('game-mp3', 1, 'capture')
    await flushTransitions()

    expect(FakeAudio.instances.at(-1)?.src).toBe('/audio/capture-chi.mp3')
    expect(FakeAudio.instances.at(-1)?.playbackRate).toBe(1)
  })

  it('keeps only the highest-priority sound for one authoritative transition', async () => {
    const service = new AudioService()
    await service.unlock()

    service.emit('game-2', 9, 'move-opponent')
    service.emit('game-2', 9, 'capture')
    service.emit('game-2', 9, 'game-win')
    await flushTransitions()

    expect(FakeAudio.instances).toHaveLength(3)
    expect(FakeAudio.instances[2].dataset.soundEvent).toBe('game-win')
  })

  it('prefers capture over a move for the same authoritative transition', async () => {
    const service = new AudioService()
    await service.unlock()

    service.emit('game-capture-priority', 3, 'move-self')
    service.emit('game-capture-priority', 3, 'capture')
    await flushTransitions()

    const played = FakeAudio.instances.filter(
      (player) => player.volume > 0 && player.play.mock.calls.length > 0,
    )
    expect(played).toHaveLength(1)
    expect(played[0].dataset.soundEvent).toBe('capture')
    expect(played[0].src).toBe('/audio/capture-chi.ogg')
  })

  it('deduplicates the same authoritative version independently for both players', async () => {
    const redService = new AudioService()
    const blackService = new AudioService()
    await redService.unlock()
    await blackService.unlock()

    redService.emitLive('game-shared', 12, ['move-self'])
    redService.emitLive('game-shared', 12, ['move-self'])
    blackService.emitLive('game-shared', 12, ['move-opponent'])
    blackService.emitLive('game-shared', 12, ['move-opponent'])
    await flushTransitions()

    const moves = FakeAudio.instances.filter(
      (player) => player.volume > 0 && player.dataset.soundEvent?.startsWith('move-'),
    )
    expect(moves).toHaveLength(2)
    expect(moves.map((player) => player.src)).toEqual([
      '/audio/move.ogg',
      '/audio/move.ogg',
    ])
  })

  it('plays a terminal capture before the result and deduplicates both events', async () => {
    const service = new AudioService()
    await service.unlock()
    FakeAudio.playbackEvents = []

    service.emitLive('game-terminal-capture', 18, ['capture', 'game-win'])
    service.emitLive('game-terminal-capture', 18, ['capture', 'game-win'])
    await flushTransitions()

    const played = FakeAudio.instances.filter(
      (player) => player.volume > 0 && player.play.mock.calls.length > 0,
    )
    expect(played.map((player) => player.dataset.soundEvent)).toEqual([
      'capture',
      'game-win',
    ])
    expect(played[0].src).toBe('/audio/capture-chi.ogg')
    expect(FakeAudio.playbackEvents).toEqual([
      'play:capture',
      'ended:capture',
      'play:game-win',
      'ended:game-win',
    ])
  })

  it('clamps persisted volume and isolates playback failures', async () => {
    const service = new AudioService()
    service.setVolume(5)
    expect(service.getSettings().volume).toBe(1)
    expect(JSON.parse(localStorage.getItem('mistchess.audio.v1') ?? '{}')).toEqual({
      enabled: true,
      volume: 1,
    })

    await service.unlock()
    FakeAudio.rejectPlayback = true
    service.emit('game-3', 2, 'capture')
    await flushTransitions()

    expect(service.getFailureCount()).toBe(1)
  })

  it('falls back from corrupt storage and suppresses muted transitions', async () => {
    localStorage.setItem('mistchess.audio.v1', '{corrupt')
    const service = new AudioService()
    expect(service.getSettings()).toEqual({ enabled: true, volume: 0.7 })

    await service.unlock()
    const playersAfterUnlock = FakeAudio.instances.length
    service.setEnabled(false)
    service.emit('game-muted', 1, 'clock-low', '10000')
    service.emit('game-muted', 2, 'game-loss')
    await flushTransitions()

    expect(FakeAudio.instances).toHaveLength(playersAfterUnlock)
    expect(JSON.parse(localStorage.getItem('mistchess.audio.v1') ?? '{}')).toEqual({
      enabled: false,
      volume: 0.7,
    })
  })
})
