import { afterEach, expect, test, vi } from 'vitest'

import { closeEntryLocks, handOverWaitMs, lockOfEntry, type HandedEntry, type HandOver, type LockParty } from './entry-lock'
import type { OutboxEntry } from './outbox'

// One tab with no Web Locks, so the lock comes at once from the mark in storage. The lock reads the lock manager when its module loads.
vi.hoisted(() => {
  Object.defineProperty(navigator, 'locks', { configurable: true, get: () => undefined })
})

afterEach(() => {
  closeEntryLocks()
  localStorage.clear()
})

const draftOf = (channel: 'raw' | 'cleaned', stateId: string): HandedEntry => {
  const entry: OutboxEntry = {
    key: `client-session.${channel}`,
    channel,
    clientSessionId: 'client-session',
    startedAt: '2026-11-06T15:30:00.000Z',
    sessionId: 'session-1',
    baseRevision: 1,
    baseText: '',
    change: { value: `words of ${channel}`, savedAt: '2026-11-06T15:31:00.000Z' },
    stateId,
  }
  return { storageKey: null, entry }
}

/** A saver of the raw channel that takes each raw draft. */
function aRawParty(takesOthers: boolean) {
  const others: HandedEntry[] = []
  const party: LockParty = {
    handOver: () => null,
    takeOver: (handed) => (handed?.drafts ?? []).filter(({ entry }) => entry.channel === 'raw').flatMap(({ entry }) => entry.stateId ?? []),
    takeLateHandOver: (handed) => handed.drafts.filter(({ entry }) => entry.channel === 'raw').flatMap(({ entry }) => entry.stateId ?? []),
    takeDraftsOfNoParty: (drafts) => {
      if (!takesOthers) return []
      others.push(...drafts)
      return drafts.flatMap(({ entry }) => entry.stateId ?? [])
    },
    noteEditingPlace: () => undefined,
    isShown: () => true,
  }
  return { party, others }
}

/** The states that the lock says it took, as the tab that gave them hears it. */
function heardTook() {
  const heard: string[][] = []
  const channel = new BroadcastChannel('recall.editors')
  channel.onmessage = ({ data }: MessageEvent<{ kind: string; states?: string[] }>) => {
    if (data.kind === 'took' && data.states) heard.push(data.states)
  }
  return { heard, close: () => channel.close() }
}

/** Each take that this tab asks of the tab that edits. */
function heardTakes() {
  const takes: string[] = []
  const channel = new BroadcastChannel('recall.editors')
  channel.onmessage = ({ data }: MessageEvent<{ kind: string; by?: string }>) => {
    if (data.kind === 'take' && data.by) takes.push(data.by)
  }
  return { takes, close: () => channel.close() }
}

const handOverOf = (...drafts: HandedEntry[]): HandOver => ({ from: 'another-tab', drafts })

test('a hand-over with words of a channel that no saver here writes gives them to a saver here, and says that this tab took each state', async () => {
  const { heard, close } = heardTook()
  const { party, others } = aRawParty(true)
  const lock = lockOfEntry('person-a', 'client-session')
  lock.join(party)
  expect(lock.editingPlace).toBe('here')

  lock.hearHandOver(handOverOf(draftOf('raw', 'another-tab.1'), draftOf('cleaned', 'another-tab.2')))

  expect(others.map(({ entry }) => entry.channel)).toEqual(['cleaned'])
  await vi.waitFor(() => expect(heard).toEqual([['another-tab.1', 'another-tab.2']]))
  close()
})

test('a state that no saver took stays with the tab that gave it, so its words are not lost', async () => {
  const { heard, close } = heardTook()
  const { party } = aRawParty(false)
  const lock = lockOfEntry('person-a', 'client-session')
  lock.join(party)

  lock.hearHandOver(handOverOf(draftOf('raw', 'another-tab.1'), draftOf('cleaned', 'another-tab.2')))

  await vi.waitFor(() => expect(heard).toEqual([['another-tab.1']]))
  close()
})

test('a second press of Edit here while the first waits for the hand-over asks once, so the wait of the first goes on', async () => {
  const { takes, close } = heardTakes()
  localStorage.setItem('recall.editor.recall.edit.person-a.client-session', 'another-tab')
  const { party } = aRawParty(true)
  const lock = lockOfEntry('person-a', 'client-session')
  lock.join(party)
  expect(lock.editingPlace).toBe('elsewhere')

  lock.takeHere()
  lock.takeHere()

  await vi.waitFor(() => expect(lock.editingPlace).toBe('here'), { timeout: handOverWaitMs * 3 })
  expect(takes).toHaveLength(1)
  close()
})
