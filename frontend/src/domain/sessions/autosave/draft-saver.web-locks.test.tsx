import { QueryClient } from '@tanstack/react-query'
import { afterEach, expect, test, vi } from 'vitest'

import { saveRawDraft } from '@/api/generated/sessions/clients/save-raw-draft'

import { draftSaverFor, resumeUnsentDrafts, type DraftTarget } from './draft-saver'
import { closeEntryLocks, handOverWaitMs, lockOfEntry, type LockParty } from './entry-lock'
import type { OutboxEntry } from './outbox'

vi.mock('@/api/generated/sessions/clients/save-raw-draft')

// These tests run the Web Locks of the browser, as a page on HTTPS has them, so a lock comes after a wait.

afterEach(() => {
  closeEntryLocks()
  localStorage.clear()
  vi.resetAllMocks()
})

const owner = 'person-a'
const target: DraftTarget = {
  owner,
  channel: 'raw',
  clientSessionId: 'client-session',
  startedAt: '2026-11-06T15:30:00.000Z',
  sessionId: 'session-1',
  baseRevision: 1,
  baseText: '',
}
const leftByAReload: OutboxEntry = {
  key: 'client-session.raw',
  channel: 'raw',
  clientSessionId: 'client-session',
  startedAt: '2026-11-06T15:30:00.000Z',
  sessionId: 'session-1',
  baseRevision: 1,
  baseText: '',
  change: { value: 'words of a reload', savedAt: '2026-11-06T15:31:00.000Z' },
}

test('a page that a dev build shows twice at its start edits the entry, and sends the words that a reload left once', async () => {
  vi.mocked(saveRawDraft).mockResolvedValue({ outcome: 'Won', draftRevision: 2, latestRevision: 2, savedAt: '2026-11-06T15:31:00+00:00', cleanupStatus: 'Clean', version: 2 } as never)
  localStorage.setItem(`recall.unsent.${owner}.a-reloaded-tab.1`, JSON.stringify(leftByAReload))
  const queryClient = new QueryClient()

  const saver = draftSaverFor(target, queryClient)
  // React in a dev build subscribes, unsubscribes, and subscribes again before the lock answers.
  saver.subscribe(() => undefined)()
  saver.subscribe(() => undefined)
  resumeUnsentDrafts(owner, queryClient)

  await expect.poll(() => saver.getSnapshot().editing).toBe('here')
  await expect.poll(() => saver.getSnapshot().status.kind).toBe('saved')
  expect(saver.lastSavedWords()).toBe('words of a reload')
  expect(saveRawDraft).toHaveBeenCalledOnce()
  expect(draftSaverFor(target, queryClient)).toBe(saver)
})

test('a page that comes back to an entry after its saver let the lock go edits the entry again', async () => {
  const queryClient = new QueryClient()
  // A saver stays in this tab for its entry, so this test takes an entry of its own.
  const saver = draftSaverFor({ ...target, clientSessionId: 'entry-of-a-return' }, queryClient)
  const leave = saver.subscribe(() => undefined)
  await expect.poll(() => saver.getSnapshot().editing).toBe('here')

  leave()
  await expect.poll(() => saver.getSnapshot().editing).toBe('waiting')
  saver.subscribe(() => undefined)

  await expect.poll(() => saver.getSnapshot().editing).toBe('here')
})

/** Another tab that holds the lock of the entry until the test frees it. */
function anotherTabHolds(name: string, options: LockOptions = {}) {
  let free = () => undefined as void
  void navigator.locks.request(name, options, () => new Promise<void>((resolve) => { free = resolve })).catch(() => undefined)
  return () => free()
}

const aShownParty: LockParty = { handOver: () => null, takeOver: () => [], takeLateHandOver: () => [], takeDraftsOfNoParty: () => [], noteEditingPlace: () => undefined, isShown: () => true }

test('Edit here that a release drops before the lock answers leaves a later Edit here working', async () => {
  const name = `recall.edit.${owner}.client-session`
  const freeFirst = anotherTabHolds(name)
  const lock = lockOfEntry(owner, 'client-session')
  lock.join(aShownParty)
  await expect.poll(() => lock.editingPlace).toBe('elsewhere')

  lock.takeHere()
  lock.release()
  // The lock manager answers the dropped steal, so the next need asks anew.
  await new Promise((resolve) => setTimeout(resolve, 100))
  lock.need()
  await expect.poll(() => lock.editingPlace).toBe('here')
  const freeSecond = anotherTabHolds(name, { steal: true })
  await expect.poll(() => lock.editingPlace).toBe('elsewhere')

  lock.takeHere()

  await expect.poll(() => lock.editingPlace, { timeout: handOverWaitMs * 4 }).toBe('here')
  freeFirst()
  freeSecond()
})
