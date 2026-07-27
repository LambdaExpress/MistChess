import { describe, expect, it } from 'vitest'
import { createClientId } from './types'

describe('createClientId', () => {
  it('creates RFC 4122 version 4 IDs without crypto.randomUUID', () => {
    const originalRandomUuid = crypto.randomUUID
    Object.defineProperty(crypto, 'randomUUID', {
      configurable: true,
      value: undefined,
    })

    try {
      const first = createClientId()
      const second = createClientId()

      expect(first).toMatch(
        /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/,
      )
      expect(second).not.toBe(first)
    } finally {
      Object.defineProperty(crypto, 'randomUUID', {
        configurable: true,
        value: originalRandomUuid,
      })
    }
  })
})
