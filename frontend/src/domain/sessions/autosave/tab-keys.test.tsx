import { afterEach, beforeEach, expect, test, vi } from 'vitest'

import { askOtherTabsToSettle, isKeyStored, keysUnder, noteThisTabLeaves, removeKey, removeLeftMarksWithNoKey, takeKeysOfTabsThatLeft, thisTab, writeKeyOfThisTab, type KeyOfATab } from './tab-keys'

const prefix = 'recall.test-keys.'

/** Another tab, as the channel sees it. */
let otherTab: BroadcastChannel

beforeEach(() => {
  otherTab = new BroadcastChannel('recall.tabs')
})

afterEach(() => {
  otherTab.close()
  for (const { storageKey } of keysUnder(prefix)) removeKey(storageKey)
  localStorage.clear()
})

const answerAsLive = (writer: string) => {
  otherTab.onmessage = ({ data }: MessageEvent<{ kind: string; nonce: string }>) => {
    if (data.kind === 'who') otherTab.postMessage({ kind: 'here', nonce: data.nonce, writer })
  }
}

async function keysTaken() {
  const taken: KeyOfATab[] = []
  await takeKeysOfTabsThatLeft(prefix, (keys) => taken.push(...keys))
  return taken.map(({ storageKey }) => storageKey)
}

test('a newer text gets a new key, and the older key goes', () => {
  const first = writeKeyOfThisTab(prefix, null, 'one')
  const second = writeKeyOfThisTab(prefix, first, 'one two')

  expect(second).not.toBe(first)
  expect(keysUnder(prefix).map(({ storageKey, stored }) => [storageKey, stored])).toEqual([[second, 'one two']])
})

test('a tab leaves the key of a tab that answers', async () => {
  answerAsLive('tab-alive')
  localStorage.setItem(`${prefix}tab-alive.1`, 'words')

  expect(await keysTaken()).toEqual([])
})

test('a tab takes the key of a tab that does not answer', async () => {
  answerAsLive('another-tab')
  localStorage.setItem(`${prefix}tab-dead.1`, 'words')

  expect(await keysTaken()).toEqual([`${prefix}tab-dead.1`])
})

test('a tab takes at once the key of a tab that said that it left, and also a key with no writer in its name', async () => {
  localStorage.setItem(`${prefix}tab-gone.1`, 'words')
  localStorage.setItem('recall.left.tab-gone', '1')
  localStorage.setItem(`${prefix}an-old-name`, 'old words')
  const taken: string[] = []

  void takeKeysOfTabsThatLeft(prefix, (keys) => taken.push(...keys.map(({ storageKey }) => storageKey)))

  expect(taken.sort()).toEqual([`${prefix}an-old-name`, `${prefix}tab-gone.1`])
})

test('a tab does not take its own keys', async () => {
  writeKeyOfThisTab(prefix, null, 'words')

  expect(await keysTaken()).toEqual([])
})

test('a mark of a tab that left goes when no key of that tab stays', () => {
  localStorage.setItem('recall.left.tab-gone', '1')
  localStorage.setItem('recall.left.tab-with-key', '1')
  localStorage.setItem(`recall.unsent.person.tab-with-key.1`, 'words')

  removeLeftMarksWithNoKey()

  expect(localStorage.getItem('recall.left.tab-gone')).toBeNull()
  expect(localStorage.getItem('recall.left.tab-with-key')).toBe('1')
})

test('a tab that leaves with a key says so, and a tab with no key leaves no mark', () => {
  noteThisTabLeaves()
  expect(localStorage.getItem(`recall.left.${thisTab}`)).toBeNull()

  writeKeyOfThisTab(prefix, null, 'words')
  noteThisTabLeaves()

  expect(localStorage.getItem(`recall.left.${thisTab}`)).toBe('1')
})

test('a sign-out waits for each tab that heard, also for a tab slower than the first answer', async () => {
  const report = { wordsOnlyInMemory: ['words of the slow tab'], hasUnsavedTags: false }
  otherTab.onmessage = ({ data }: MessageEvent<{ kind: string; nonce: string }>) => {
    if (data.kind !== 'settle') return
    otherTab.postMessage({ kind: 'settled', nonce: data.nonce, writer: 'fast-tab', report: { wordsOnlyInMemory: [], hasUnsavedTags: false } })
    setTimeout(() => otherTab.postMessage({ kind: 'settling', nonce: data.nonce, writer: 'slow-tab' }), 100)
    setTimeout(() => otherTab.postMessage({ kind: 'settled', nonce: data.nonce, writer: 'slow-tab', report }), 800)
  }

  const reports = await askOtherTabsToSettle(3000)

  expect(reports).toContainEqual(report)
})

test('a browser with no BroadcastChannel takes the keys of other tabs, and no call throws', async () => {
  const realChannel = globalThis.BroadcastChannel
  vi.resetModules()
  vi.stubGlobal('BroadcastChannel', undefined)
  const keys = await import('./tab-keys')
  localStorage.setItem(`${prefix}another-tab.1`, 'words')

  expect(() => keys.joinTheOtherTabs(() => Promise.resolve({ wordsOnlyInMemory: [], hasUnsavedTags: false }))).not.toThrow()
  const taken: string[] = []
  await keys.takeKeysOfTabsThatLeft(prefix, (found) => taken.push(...found.map(({ storageKey }) => storageKey)))
  await expect(keys.askOtherTabsToSettle(50)).resolves.toEqual([])
  expect(() => keys.noteThisTabLeaves()).not.toThrow()

  expect(taken).toEqual([`${prefix}another-tab.1`])
  vi.stubGlobal('BroadcastChannel', realChannel)
  vi.unstubAllGlobals()
})

test('a failed read of a key says that the key is still stored, so a tab does not treat its words as gone', () => {
  const stored = writeKeyOfThisTab(prefix, null, 'words') as string
  expect(isKeyStored(stored)).toBe(true)
  removeKey(stored)
  expect(isKeyStored(stored)).toBe(false)

  vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
    throw new DOMException('blocked', 'SecurityError')
  })
  expect(isKeyStored(stored)).toBe(true)
  vi.restoreAllMocks()
})
