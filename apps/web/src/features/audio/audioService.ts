export type SoundEvent =
  | 'match-found'
  | 'game-start'
  | 'move-self'
  | 'move-opponent'
  | 'capture'
  | 'clock-low'
  | 'game-win'
  | 'game-loss'
  | 'game-draw'

export type AudioSettings = {
  enabled: boolean
  volume: number
}

type QueuedSound = {
  key: string
  event: SoundEvent
}

const SETTINGS_KEY = 'mistchess.audio.v1'
const TERMINAL_EVENTS: Partial<Record<SoundEvent, true>> = {
  'game-win': true,
  'game-loss': true,
  'game-draw': true,
}
const DEFAULT_SETTINGS: AudioSettings = { enabled: true, volume: 0.7 }
const EVENT_PRIORITY: Record<SoundEvent, number> = {
  'match-found': 2,
  'game-start': 1,
  'move-self': 1,
  'move-opponent': 1,
  capture: 2,
  'clock-low': 2,
  'game-win': 3,
  'game-loss': 3,
  'game-draw': 3,
}
const SOUND_ASSET: Record<SoundEvent, 'mistchess-tone' | 'move' | 'capture'> = {
  'match-found': 'mistchess-tone',
  'game-start': 'mistchess-tone',
  'move-self': 'move',
  'move-opponent': 'move',
  capture: 'capture',
  'clock-low': 'mistchess-tone',
  'game-win': 'mistchess-tone',
  'game-loss': 'mistchess-tone',
  'game-draw': 'mistchess-tone',
}
const PLAYBACK_RATE: Record<SoundEvent, number> = {
  'match-found': 1.35,
  'game-start': 1.1,
  'move-self': 1,
  'move-opponent': 1,
  capture: 1,
  'clock-low': 1.55,
  'game-win': 1.3,
  'game-loss': 0.55,
  'game-draw': 0.78,
}

export class AudioService {
  private settings = this.loadSettings()
  private readonly listeners = new Set<() => void>()
  private readonly playedKeys = new Set<string>()
  private readonly pendingTransitions = new Map<string, QueuedSound>()
  private queuedImportant: QueuedSound | null = null
  private unlocked = false
  private extension: 'ogg' | 'mp3' | null = null
  private diagnosticFailures = 0

  getSettings = (): AudioSettings => this.settings

  subscribe = (listener: () => void) => {
    this.listeners.add(listener)
    return () => this.listeners.delete(listener)
  }

  setEnabled(enabled: boolean) {
    this.updateSettings({ ...this.settings, enabled })
  }

  setVolume(volume: number) {
    const normalized = Number.isFinite(volume)
      ? Math.min(1, Math.max(0, volume))
      : DEFAULT_SETTINGS.volume
    this.updateSettings({ ...this.settings, volume: normalized })
  }

  async unlock() {
    if (this.unlocked || typeof Audio === 'undefined') return
    const probe = this.createPlayer('game-start')
    probe.volume = 0
    try {
      await probe.play()
      probe.pause()
      probe.currentTime = 0
      this.unlocked = true
      const queued = this.queuedImportant
      this.queuedImportant = null
      if (queued) this.play(queued)
    } catch {
      this.diagnosticFailures += 1
    }
  }

  emit(gameId: string, version: number, event: SoundEvent, discriminator = '') {
    const transitionKey = `${gameId}:${version}`
    const key = `${transitionKey}:${event}:${discriminator}`
    if (this.playedKeys.has(key)) return

    const queued = { key, event }
    const existing = this.pendingTransitions.get(transitionKey)
    if (!existing || EVENT_PRIORITY[event] > EVENT_PRIORITY[existing.event]) {
      this.pendingTransitions.set(transitionKey, queued)
    }
    queueMicrotask(() => {
      const pending = this.pendingTransitions.get(transitionKey)
      if (!pending) return
      this.pendingTransitions.delete(transitionKey)
      this.play(pending)
    })
  }

  getFailureCount() {
    return this.diagnosticFailures
  }
  private play(sound: QueuedSound) {
    if (this.playedKeys.has(sound.key)) return
    if (!this.settings.enabled || this.settings.volume === 0) {
      this.rememberPlayed(sound.key)
      return
    }

    if (!this.unlocked) {
      const important = Boolean(TERMINAL_EVENTS[sound.event]) ||
        sound.event === 'match-found' ||
        sound.event === 'game-start'
      if (!important) {
        this.rememberPlayed(sound.key)
        return
      }

      if (
        !this.queuedImportant ||
        EVENT_PRIORITY[sound.event] >= EVENT_PRIORITY[this.queuedImportant.event]
      ) {
        if (this.queuedImportant && this.queuedImportant.key !== sound.key) {
          this.rememberPlayed(this.queuedImportant.key)
        }
        this.queuedImportant = sound
      } else {
        this.rememberPlayed(sound.key)
      }
      return
    }

    this.rememberPlayed(sound.key)
    const player = this.createPlayer(sound.event)
    player.volume = this.settings.volume
    player.playbackRate = PLAYBACK_RATE[sound.event]
    void player.play().catch(() => {
      this.diagnosticFailures += 1
    })
  }

  private rememberPlayed(key: string) {
    this.playedKeys.add(key)
    if (this.playedKeys.size > 512) {
      const oldest = this.playedKeys.values().next().value
      if (oldest) this.playedKeys.delete(oldest)
    }
  }

  private createPlayer(event: SoundEvent) {
    const extension = this.extension ?? this.detectExtension()
    const player = new Audio(`/audio/${SOUND_ASSET[event]}.${extension}`)
    player.preload = 'auto'
    player.dataset.soundEvent = event
    return player
  }

  private detectExtension(): 'ogg' | 'mp3' {
    const probe = new Audio()
    this.extension = probe.canPlayType('audio/ogg; codecs="vorbis"') ? 'ogg' : 'mp3'
    return this.extension
  }

  private updateSettings(settings: AudioSettings) {
    this.settings = settings
    try {
      localStorage.setItem(SETTINGS_KEY, JSON.stringify(settings))
    } catch {
      // Storage failures do not affect gameplay or sound playback in this tab.
    }
    this.listeners.forEach((listener) => listener())
  }

  private loadSettings(): AudioSettings {
    try {
      const parsed = JSON.parse(localStorage.getItem(SETTINGS_KEY) ?? '') as Partial<AudioSettings>
      if (
        typeof parsed.enabled === 'boolean' &&
        typeof parsed.volume === 'number' &&
        Number.isFinite(parsed.volume)
      ) {
        return {
          enabled: parsed.enabled,
          volume: Math.min(1, Math.max(0, parsed.volume)),
        }
      }
    } catch {
      // Corrupt or unavailable storage falls back to stable defaults.
    }
    return DEFAULT_SETTINGS
  }
}

export const audioService = new AudioService()