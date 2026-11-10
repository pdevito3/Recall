import { QueryClient } from '@tanstack/react-query'
import { afterEach, expect, test, vi } from 'vitest'

import { getSession } from '@/api/generated/sessions/clients/get-session'
import { saveRawDraft } from '@/api/generated/sessions/clients/save-raw-draft'

import { draftSaverFor, resumeUnsentDrafts, type DraftTarget } from './draft-saver'
import { closeEntryLocks, handOverWaitMs, lockOfEntry, queryWaitMs, type LockParty } from './entry-lock'
import type { OutboxEntry } from './outbox'

vi.mock('@/api/generated/sessions/clients/get-session')
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

/** Another tab asks for the lock, and the promise ends once the lock manager gave it the lock. */
function anotherTabGets(name: string, options: LockOptions = {}) {
  return new Promise<() => void>((granted) => {
    void navigator.locks.request(name, options, () => new Promise<void>((free) => granted(() => free()))).catch(() => undefined)
  })
}

const aShownParty: LockParty = { handOver: () => null, takeOver: () => [], takeLateHandOver: () => [], takeDraftsOfNoParty: () => [], noteEditingPlace: () => undefined, isShown: () => true }

test('a page that gets the entry back from a tab that sent each word sends its next words', async () => {
  vi.mocked(getSession).mockResolvedValue({ id: 'session-1', rawText: 'words of the other tab', rawDraftRevision: 2 } as never)
  vi.mocked(saveRawDraft).mockResolvedValue({ outcome: 'Won', draftRevision: 3, latestRevision: 3, savedAt: '2026-11-06T15:32:00+00:00', cleanupStatus: 'Clean', version: 3 } as never)
  const saver = draftSaverFor({ ...target, clientSessionId: 'entry-taken-back' }, new QueryClient())
  saver.subscribe(() => undefined)
  await expect.poll(() => saver.getSnapshot().editing).toBe('here')
  // Another tab takes the entry with Edit here, sends each word, and closes.
  const closeOther = anotherTabHolds(`recall.edit.${owner}.entry-taken-back`, { steal: true })
  await expect.poll(() => saver.getSnapshot().editing).toBe('elsewhere')
  closeOther()
  await expect.poll(() => saver.getSnapshot().editing).toBe('here')

  saver.edit('words of the other tab and more')
  saver.flush()

  await expect.poll(() => vi.mocked(saveRawDraft).mock.calls.length).toBe(1)
  await expect.poll(() => saver.getSnapshot().status.kind).toBe('saved')
})

test('a take from a tab that holds no lock leaves the editor editing, with its words', async () => {
  const lock = lockOfEntry(owner, 'entry-of-a-take-with-no-lock')
  let handOvers = 0
  lock.join({ ...aShownParty, handOver: () => ((handOvers += 1), null) })
  await expect.poll(() => lock.editingPlace).toBe('here')

  // A tab of an older build wakes with a grant of Edit here that another tab stole since, and asks for the words.
  lock.hearTake('a-tab-with-an-old-grant')

  expect(lock.editingPlace).toBe('here')
  expect(handOvers).toBe(0)
})

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

test('Edit here whose grant a third tab steals before the lock manager answers waits in line again, and edits when that tab closes', async () => {
  const name = `recall.edit.${owner}.entry-of-a-stale-steal`
  const freeEditor = anotherTabHolds(name)
  const lock = lockOfEntry(owner, 'entry-of-a-stale-steal')
  lock.join(aShownParty)
  await expect.poll(() => lock.editingPlace).toBe('elsewhere')
  // The grant of Edit here waits for the answer of the lock manager, as in a tab that froze, and a third tab steals the lock meanwhile.
  let freeThief = () => undefined as void
  const query = navigator.locks.query.bind(navigator.locks)
  const barrier = vi.spyOn(navigator.locks, 'query').mockImplementationOnce(async () => {
    freeThief = await anotherTabGets(name, { steal: true })
    return query()
  })

  lock.takeHere()
  await expect.poll(() => barrier.mock.results[0]?.type).toBe('return')
  await barrier.mock.results[0]?.value
  expect(lock.editingPlace).toBe('elsewhere')
  freeThief()

  await expect.poll(() => lock.editingPlace, { timeout: handOverWaitMs * 4 }).toBe('here')
  barrier.mockRestore()
  freeEditor()
})

test.each(['rejects', 'never answers'])('Edit here after a query of the lock manager that %s works again with no reload', async (fault) => {
  const name = `recall.edit.${owner}.entry-of-a-query-that-${fault.replace(' ', '-')}`
  const freeEditor = anotherTabHolds(name)
  const lock = lockOfEntry(owner, `entry-of-a-query-that-${fault.replace(' ', '-')}`)
  lock.join(aShownParty)
  await expect.poll(() => lock.editingPlace).toBe('elsewhere')
  let editorIsBack: Promise<() => void> = new Promise(() => undefined)
  const barrier = vi.spyOn(navigator.locks, 'query').mockImplementationOnce(() => {
    // The tab that lost the entry to the steal waits in line for it again, as each tab does.
    editorIsBack = anotherTabGets(name)
    return fault === 'rejects' ? Promise.reject(new DOMException('a fault', 'InvalidStateError')) : new Promise(() => undefined)
  })
  lock.takeHere()
  await expect.poll(() => barrier).toHaveBeenCalledOnce()
  const freeEditorAgain = await Promise.race([editorIsBack, new Promise<never>((_, reject) => setTimeout(() => reject(new Error('the editor did not get the entry back')), queryWaitMs * 2))])
  expect(lock.editingPlace).toBe('elsewhere')
  barrier.mockRestore()

  lock.takeHere()

  await expect.poll(() => lock.editingPlace, { timeout: handOverWaitMs * 4 }).toBe('here')
  freeEditor()
  freeEditorAgain()
}, queryWaitMs * 4)

test.each(['rejects', 'never answers'])('a new entry whose query of the lock manager %s keeps its one editor', async (fault) => {
  const entry = `new-entry-of-a-query-that-${fault.replace(' ', '-')}`
  let freeOther: (() => void) | null = null
  const barrier = vi.spyOn(navigator.locks, 'query').mockImplementationOnce(() => {
    // Another tab opens the entry meanwhile, and waits in line for it.
    void anotherTabGets(`recall.edit.${owner}.${entry}`).then((free) => {
      freeOther = free
    })
    return fault === 'rejects' ? Promise.reject(new DOMException('a fault', 'InvalidStateError')) : new Promise(() => undefined)
  })
  const lock = lockOfEntry(owner, entry, true)
  lock.join(aShownParty)
  await expect.poll(() => barrier).toHaveBeenCalledOnce()

  // The wait for the query ends, and the lock manager has a moment to give the lock to a tab in line.
  await new Promise((resolve) => setTimeout(resolve, (fault === 'rejects' ? 0 : queryWaitMs) + 500))

  expect(lock.editingPlace).toBe('here')
  expect(freeOther).toBeNull()
  barrier.mockRestore()
  closeEntryLocks()
  await expect.poll(() => freeOther).not.toBeNull()
  freeOther?.()
}, queryWaitMs * 4)
