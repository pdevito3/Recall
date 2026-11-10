import { afterEach, expect, test, vi } from 'vitest'

import { openChannel } from './browser-channel'

afterEach(() => {
  vi.unstubAllGlobals()
})

test('a browser with no BroadcastChannel gives no channel, and nothing throws', () => {
  vi.stubGlobal('BroadcastChannel', undefined)

  expect(openChannel('recall.test')).toBeNull()
})

test('a browser that refuses to make a channel gives no channel, and nothing throws', () => {
  vi.stubGlobal('BroadcastChannel', function refuses() {
    throw new DOMException('blocked', 'SecurityError')
  })

  expect(openChannel('recall.test')).toBeNull()
})
