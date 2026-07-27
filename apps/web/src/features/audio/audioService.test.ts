import { beforeEach, describe, expect, it, vi } from 'vitest'
import { AudioService } from './audioService'

class FakeAudio {
  static instances: FakeAudio[] = []
  static rejectPlayback = false

  readonly dataset: Record<string, string> = {}
  readonly src: string
  currentTime = 0
  playbackRate = 1
  preload = ''
  volume = 1
  readonly pause = vi.fn()
  readonly play = vi.fn(() => FakeAudio.rejectPlayback
    ? Promise.reject(new Error('playback failed'))
    : Promise.resolve())

  constructor(src = '') {
    this.src = src
    FakeAudio.instances.push(this)
  }

  canPlayType() {
    return 'probably'
  }
}

async function flushTransitions() {
  await Promise.resolve()
  await Promise.resolve()
}

beforeEach(() => {
  localStorage.clear()
  FakeAudio.instances = []
  FakeAudio.rejectPlayback = false
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
