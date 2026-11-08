import { QueryClient } from '@tanstack/react-query'
import { afterEach, expect, test, vi } from 'vitest'

import { addSession } from '@/api/generated/sessions/clients/add-session'
import { getRawRevision } from '@/api/generated/sessions/clients/get-raw-revision'
import { getSession } from '@/api/generated/sessions/clients/get-session'
import type { SessionDto } from '@/api/generated/models/types/session-dto'
import { saveCleanedDraft } from '@/api/generated/sessions/clients/save-cleaned-draft'
import { saveRawDraft } from '@/api/generated/sessions/clients/save-raw-draft'
import { ProblemDetailsError } from '@/lib/problem-details'

import { SessionKeys } from '../apis/session.keys'
import { plainWordsOf } from '../editor/content'
import { DraftSaver, draftNotices, waitAfterLosses, draftSaverFor, removeNoticesOfWordsInHistory, shownStatusOf, unsentWordsOnThisDevice, type DraftTarget } from './draft-saver'
import { closeEntryLocks } from './entry-lock'
import { readOutbox, type OutboxEntry, type StoredEntry } from './outbox'
import { thisTab } from './tab-keys'

vi.mock('@/api/generated/sessions/clients/add-session')
vi.mock('@/api/generated/sessions/clients/get-raw-revision')
vi.mock('@/api/generated/sessions/clients/get-session')
vi.mock('@/api/generated/sessions/clients/save-cleaned-draft')
vi.mock('@/api/generated/sessions/clients/save-raw-draft')

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

// These tests run one tab with no Web Locks, as on plain HTTP, so the lock of an entry comes at once from the mark in
// storage. The lock reads the lock manager when its module loads, so this runs before the imports. The model test and
// the end-to-end tests run Web Locks.
vi.hoisted(() => {
  Object.defineProperty(navigator, 'locks', { configurable: true, get: () => undefined })
})

const aSaver = () => new DraftSaver(target, new QueryClient())
/** A page gives the text of the Draft at the base, so a merge after a loss knows the change of the person. */
const fromBase: DraftTarget = { ...target, baseText: JSON.stringify({ type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'base' }] }] }) }

afterEach(() => {
  closeEntryLocks()
  localStorage.clear()
  vi.restoreAllMocks()
  vi.resetAllMocks()
})

const key = 'client-session.raw'
const entryWith = (words: string, more: Partial<OutboxEntry> = {}): OutboxEntry => ({
  key,
  channel: 'raw',
  clientSessionId: target.clientSessionId,
  startedAt: target.startedAt,
  sessionId: target.sessionId,
  baseRevision: 1,
  baseText: '',
  change: { value: words, savedAt: '2026-11-06T15:31:00.000Z' },
  ...more,
})
const storedByAnotherTab = (entry: OutboxEntry): StoredEntry => {
  const storageKey = `recall.unsent.${owner}.another-tab.1`
  localStorage.setItem(storageKey, JSON.stringify(entry))
  return { storageKey, writer: 'another-tab', entry }
}
const wordsInKeys = () => readOutbox(owner).map(({ entry }) => entry.change?.value ?? entry.held)
const wonAt = (revision: number) => ({ outcome: 'Won', draftRevision: revision, latestRevision: revision, savedAt: '2026-11-06T15:31:00+00:00', cleanupStatus: 'Clean', version: revision }) as never
const lostAt = (revision: number) => ({ outcome: 'Lost', draftRevision: revision, latestRevision: revision + 1, savedAt: '2026-11-06T15:31:00+00:00', cleanupStatus: 'Clean', version: revision }) as never
const entryWithWords = (rawText: string, revision = 1) => ({ id: 'session-1', rawText, rawDraftRevision: revision, rawLatestRevision: revision }) as SessionDto
const clientAnswering = (rawText: string, revision = 1) => {
  const queryClient = new QueryClient()
  vi.mocked(getSession).mockResolvedValue(entryWithWords(rawText, revision))
  return queryClient
}
const fullStorage = () =>
  vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
    throw new DOMException('full', 'QuotaExceededError')
  })

test('a discarded saver writes no words back to storage', () => {
  const saver = aSaver()

  saver.discard()
  saver.edit('words of a discard')

  expect(readOutbox(owner)).toEqual([])
})

/** A browser quota: nothing new fits, and a key that a page just removed fits again. */
function fullQuota() {
  const removed = new Set<string>()
  const remove = Storage.prototype.removeItem
  const write = Storage.prototype.setItem
  vi.spyOn(Storage.prototype, 'removeItem').mockImplementation(function (this: Storage, name) {
    removed.add(this.getItem(name) ?? '')
    remove.call(this, name)
  })
  return vi.spyOn(Storage.prototype, 'setItem').mockImplementation(function (this: Storage, name, value) {
    if (!removed.has(value)) throw new DOMException('full', 'QuotaExceededError')
    write.call(this, name, value)
  })
}

test('a stopped saver keeps its words in storage', () => {
  const saver = aSaver()

  saver.stop()
  saver.edit('words at a logout')

  expect(wordsInKeys()).toEqual(['words at a logout'])
})

test('the end of a delete does not undo a stop, so a late 404 makes no new entry', async () => {
  let answer: (error: Error) => void = () => undefined
  vi.mocked(saveRawDraft).mockImplementation(() => new Promise((_, reject) => { answer = reject }))
  const saver = aSaver()
  saver.edit('words of a deleted entry')
  saver.flush()
  await vi.waitFor(() => expect(saveRawDraft).toHaveBeenCalledOnce())

  saver.stop()
  saver.noteDelete(false)
  answer(new ProblemDetailsError({ type: 'about:blank', title: 'Gone', status: 404 }))
  await new Promise((resolve) => setTimeout(resolve))

  expect(addSession).not.toHaveBeenCalled()
  expect(saver.getSnapshot().movedFromDeletedEntry).toBe(false)
})

test('each write of a saver gets a new key, and only one key stays', () => {
  vi.mocked(saveRawDraft).mockReturnValue(new Promise(() => undefined))
  const saver = aSaver()

  saver.edit('first')
  const [{ storageKey: firstKey } = { storageKey: '' }] = readOutbox(owner)
  saver.edit('first and more')
  const stored = readOutbox(owner)

  expect(stored.map(({ entry }) => entry.change?.value)).toEqual(['first and more'])
  expect(stored[0]?.storageKey).not.toBe(firstKey)
  saver.stop()
})

test('a discard removes only the words of its saver, also on the same draft', () => {
  vi.mocked(saveRawDraft).mockReturnValue(new Promise(() => undefined))
  const shown = aSaver()
  const other = aSaver()
  shown.edit('words that a notice showed')
  other.edit('words that no notice showed')

  shown.discard()

  expect(wordsInKeys()).toEqual(['words that no notice showed'])
  other.stop()
})

test('a saver that takes the text of a tab that left removes the key of that tab in the same write', () => {
  vi.mocked(saveRawDraft).mockReturnValue(new Promise(() => undefined))
  const stored = storedByAnotherTab(entryWith('words of a closed tab'))

  const saver = new DraftSaver(target, new QueryClient())

  expect(localStorage.getItem(stored.storageKey)).toBeNull()
  expect(wordsInKeys()).toEqual(['words of a closed tab'])
  saver.stop()
})

test('a taken text on a full storage stays in the key of the tab that left', () => {
  vi.mocked(saveRawDraft).mockReturnValue(new Promise(() => undefined))
  storedByAnotherTab(entryWith('words of a closed tab'))
  const setItem = vi.spyOn(Storage.prototype, 'setItem')
  setItem.mockImplementationOnce(() => {
    throw new DOMException('full', 'QuotaExceededError')
  })

  const saver = new DraftSaver(target, new QueryClient())

  expect(wordsInKeys()).toEqual(['words of a closed tab'])
  saver.stop()
})

test('one write moves held words to the queue, so no moment has them in two places or in none', () => {
  vi.mocked(saveRawDraft).mockReturnValue(new Promise(() => undefined))
  const saver = aSaver()
  saver.holdInOutbox('held words')
  expect(readOutbox(owner)[0]?.entry).toMatchObject({ change: null, held: 'held words' })

  const joined = saver.joinHoldInto('words on the server')

  expect(saver.wordsInHold()).toBeNull()
  expect(readOutbox(owner)).toHaveLength(1)
  expect(readOutbox(owner)[0]?.entry).toMatchObject({ held: null, change: { value: joined } })
  saver.stop()
})

test('a key on another page during a hold leaves the held words in the same key', () => {
  vi.mocked(saveRawDraft).mockReturnValue(new Promise(() => undefined))
  const saver = aSaver()

  saver.holdInOutbox('held words')
  saver.edit('a key on the entry page')

  expect(readOutbox(owner)).toHaveLength(1)
  expect(readOutbox(owner)[0]?.entry).toMatchObject({ held: 'held words', change: { value: 'a key on the entry page' } })
  saver.stop()
})

test('a join with no page puts the held words after the newest words of the person, and tells an open page', async () => {
  vi.mocked(saveRawDraft).mockReturnValue(new Promise(() => undefined))
  const saver = new DraftSaver(target, clientAnswering('words on the server'))
  const shown = vi.fn()
  saver.subscribeToNewWords(shown)
  saver.holdInOutbox('held words')
  saver.edit('a key on the entry page')

  await saver.joinHoldWithNoPage()

  const joined = readOutbox(owner)[0]?.entry.change?.value ?? ''
  expect(joined).toContain('a key on the entry page')
  expect(joined).toContain('held words')
  expect(shown).toHaveBeenCalledWith(joined, 'heldWordsJoined')
  expect(saver.notice()).toBeNull()
  saver.stop()
})

test('words that return at a later sign-in and join an entry with no page raise a notice', async () => {
  vi.mocked(saveRawDraft).mockResolvedValue(wonAt(2))
  storedByAnotherTab(entryWith('', { change: null, held: 'words of an earlier sign-in' }))

  const saver = new DraftSaver(target, clientAnswering('words on the server'))

  await vi.waitFor(() => expect(saver.notice()).toMatchObject({ kind: 'joined', sessionId: 'session-1' }))
  expect(plainWordsOf(vi.mocked(saveRawDraft).mock.lastCall?.[0].body.text ?? '')).toContain('words of an earlier sign-in')
  saver.dismissJoinNotice()
  expect(saver.notice()).toBeNull()
  saver.stop()
})

test('a full storage tells the person at once that the words are only in this page, and a later write ends the notice', () => {
  const setItem = fullStorage()
  vi.mocked(saveRawDraft).mockReturnValue(new Promise(() => undefined))
  const saver = aSaver()
  saver.subscribe(() => undefined)

  saver.edit('words with no room')

  expect(saver.notice()).toMatchObject({ kind: 'onlyInMemory', words: 'words with no room', reason: null })
  expect(saver.isHoldingWordsOnlyInMemory()).toBe(true)

  setItem.mockRestore()
  saver.edit('words with room')

  expect(saver.notice()).toBeNull()
  saver.stop()
})

test('a Kept save whose words left the Draft, on a full storage, shows the words in History, and no notice with no words', async () => {
  fullStorage()
  vi.mocked(saveRawDraft).mockResolvedValue(keptAt(2))
  const queryClient = new QueryClient()
  vi.mocked(getSession).mockResolvedValue(entryWithWords('newer words', 7))
  const saver = new DraftSaver(target, queryClient)

  saver.edit('my words')
  saver.flush()

  await vi.waitFor(() => expect(saver.notice()).toMatchObject({ kind: 'inHistory', words: 'my words' }))
  saver.stop()
})

test('with no room for two copies, a longer text replaces the older key', () => {
  vi.mocked(saveRawDraft).mockReturnValue(new Promise(() => undefined))
  const saver = aSaver()
  saver.edit('words')
  const setItem = vi.spyOn(Storage.prototype, 'setItem')
  setItem.mockImplementationOnce(() => {
    throw new DOMException('full', 'QuotaExceededError')
  })

  saver.edit('words and more')

  expect(wordsInKeys()).toEqual(['words and more'])
  expect(saver.isHoldingWordsOnlyInMemory()).toBe(false)
  saver.stop()
})

test('with no room at all, the older key goes, so no tab sends its older words again, and the person sees a notice', () => {
  vi.mocked(saveRawDraft).mockReturnValue(new Promise(() => undefined))
  const saver = aSaver()
  saver.edit('words')
  const real = Storage.prototype.setItem
  const setItem = vi.spyOn(Storage.prototype, 'setItem')
  setItem.mockImplementationOnce(() => {
    throw new DOMException('full', 'QuotaExceededError')
  })
  setItem.mockImplementationOnce(() => {
    throw new DOMException('full', 'QuotaExceededError')
  })
  setItem.mockImplementationOnce(function (this: Storage, name, value) {
    real.call(this, name, value)
  })

  saver.edit('words and more')

  expect(wordsInKeys()).toEqual([])
  expect(saver.isHoldingWordsOnlyInMemory()).toBe(true)
  expect(saver.notice()).toMatchObject({ kind: 'onlyInMemory', words: 'words and more' })
  saver.stop()
})

test('with no room, a write that changes no words keeps the key, so the words stay in storage', async () => {
  vi.mocked(saveRawDraft).mockReturnValue(new Promise(() => undefined))
  const saver = aSaver()
  saver.edit('words')
  const real = Storage.prototype.setItem
  const setItem = vi.spyOn(Storage.prototype, 'setItem')
  const full = () => {
    throw new DOMException('full', 'QuotaExceededError')
  }
  // The new key and the key in place of the older key find no room, and the older key goes back.
  setItem.mockImplementationOnce(full).mockImplementationOnce(full).mockImplementationOnce(function (this: Storage, name, value) {
    real.call(this, name, value)
  })

  // The send changes only the change in flight, and no words.
  saver.flush()
  await vi.waitFor(() => expect(saveRawDraft).toHaveBeenCalledOnce())

  expect(wordsInKeys()).toEqual(['words'])
  expect(saver.isHoldingWordsOnlyInMemory()).toBe(false)
  saver.stop()
})

test('a take of the key of another tab keeps that key when the first write finds no room, so the words stay in storage', () => {
  vi.mocked(saveRawDraft).mockReturnValue(new Promise(() => undefined))
  const saver = aSaver()
  const entry = entryWith('handed words', { stateId: 'another-tab.3' })
  const { storageKey } = storedByAnotherTab(entry)
  const real = Storage.prototype.setItem
  // Storage is full: a new key finds no room, and only the handed key fits back in its own room.
  vi.spyOn(Storage.prototype, 'setItem').mockImplementation(function (this: Storage, name, value) {
    if (name !== storageKey) throw new DOMException('full', 'QuotaExceededError')
    real.call(this, name, value)
  })

  saver.takeOver({ from: 'another-tab', drafts: [{ storageKey, entry }] })

  expect(wordsInKeys()).toEqual(['handed words'])
  expect(saver.isHoldingWordsOnlyInMemory()).toBe(false)
  saver.stop()
})

test('a hand-over of words of a channel with no saver here goes with its own channel and base, so no word of it is lost', async () => {
  vi.mocked(saveCleanedDraft).mockReturnValue(new Promise(() => undefined))
  const saver = aSaver()
  const cleaned = entryWith('cleaned words', { key: 'client-session.cleaned', channel: 'cleaned', stateId: 'another-tab.4', baseRevision: 2 })

  expect(saver.takeDraftsOfNoParty([{ storageKey: null, entry: cleaned }])).toEqual(['another-tab.4'])

  await vi.waitFor(() => expect(saveCleanedDraft).toHaveBeenCalledOnce())
  expect(vi.mocked(saveCleanedDraft).mock.calls[0]?.[0].body).toMatchObject({ text: 'cleaned words', baseRevision: 2 })
  expect(saveRawDraft).not.toHaveBeenCalled()
  expect(readOutbox(owner).map(({ entry }) => entry.channel)).toEqual(['cleaned'])
  saver.stop()
})

test('a restore that finds no room leaves the words in the page, with the notice', () => {
  vi.mocked(saveRawDraft).mockReturnValue(new Promise(() => undefined))
  const saver = aSaver()
  saver.edit('words')
  fullStorage()

  expect(() => saver.edit('words and more')).not.toThrow()

  expect(saver.notice()).toMatchObject({ kind: 'onlyInMemory', words: 'words and more' })
  saver.stop()
})

test('a stopped saver no longer warns of a loss, because the person chose', () => {
  fullStorage()
  const saver = aSaver()
  saver.edit('words with no room')

  saver.stop()

  expect(saver.isHoldingWordsOnlyInMemory()).toBe(false)
})

test('a save that waits with no page open has a notice until it succeeds', async () => {
  vi.useFakeTimers()
  vi.mocked(saveRawDraft).mockRejectedValue(new Error('Network Error'))
  const saver = aSaver()

  saver.edit('words after Done')
  saver.flush()
  await vi.advanceTimersByTimeAsync(10)

  expect(saver.notice()).toMatchObject({ kind: 'waiting', words: 'words after Done' })
  const stop = saver.subscribe(() => undefined)
  expect(saver.notice()).toBeNull()
  stop()
  expect(saver.notice()).toMatchObject({ kind: 'waiting' })

  vi.mocked(saveRawDraft).mockResolvedValue(wonAt(2))
  await vi.advanceTimersByTimeAsync(40_000)

  expect(saver.notice()).toBeNull()
  saver.stop()
  vi.useRealTimers()
})

test('the notice has the newest held words at once, and also the words of the queue', () => {
  vi.mocked(saveRawDraft).mockReturnValue(new Promise(() => undefined))
  const saver = draftSaverFor(target, new QueryClient())
  saver.takeHold()
  const setItem = fullStorage()

  saver.holdInOutbox('first')
  saver.holdInOutbox('first and more')
  expect(draftNotices()[0]).toMatchObject({ kind: 'onlyInMemory', words: 'first and more' })

  saver.edit('a key on another page')
  const [notice] = draftNotices()
  const words = notice?.kind === 'onlyInMemory' ? plainWordsOf(notice.words) : ''
  expect(words).toContain('first and more')
  expect(words).toContain('a key on another page')

  setItem.mockRestore()
  saver.discard()
})

test('an open page offers no discard of its words, because the page would stop saving', () => {
  fullStorage()
  vi.mocked(saveRawDraft).mockReturnValue(new Promise(() => undefined))
  const saver = aSaver()
  saver.edit('words with no room')

  expect(saver.notice()).toMatchObject({ kind: 'onlyInMemory', canDiscard: true })
  const stop = saver.subscribe(() => undefined)
  expect(saver.notice()).toMatchObject({ kind: 'onlyInMemory', canDiscard: false })
  stop()
  saver.stop()
})

test('a move to a new entry on a full storage removes the key of the old entry, and the person sees that the words are only on this page', async () => {
  storedByAnotherTab(entryWith('saved words'))
  fullQuota()
  vi.mocked(saveRawDraft).mockRejectedValueOnce(new ProblemDetailsError({ type: 'about:blank', title: 'Gone', status: 404 }))
  vi.mocked(addSession).mockReturnValue(new Promise(() => undefined))

  const saver = new DraftSaver(target, new QueryClient())
  saver.flush()
  await vi.waitFor(() => expect(saver.getSnapshot().movedFromDeletedEntry).toBe(true))

  expect(wordsInKeys()).toEqual([])
  expect(saver.isHoldingWordsOnlyInMemory()).toBe(true)
  // The move put the saver in the list of this tab, so the next test must not see it.
  saver.discard()
})

test('after a move on a full storage, a next write replaces the old copy, and a discard removes it', async () => {
  storedByAnotherTab(entryWith('saved words'))
  const setItem = fullQuota()
  vi.mocked(saveRawDraft).mockRejectedValueOnce(new ProblemDetailsError({ type: 'about:blank', title: 'Gone', status: 404 }))
  vi.mocked(addSession).mockReturnValue(new Promise(() => undefined))
  const saver = new DraftSaver(target, new QueryClient())
  saver.flush()
  await vi.waitFor(() => expect(saver.getSnapshot().movedFromDeletedEntry).toBe(true))
  setItem.mockRestore()

  saver.edit('saved words and more')
  expect(wordsInKeys()).toEqual(['saved words and more'])

  saver.discard()
  expect(readOutbox(owner)).toEqual([])
})

test('after a loss, the page reads the Draft, merges the change of the person into it one time, and saves from the number of the read', async () => {
  vi.mocked(saveRawDraft).mockResolvedValueOnce(lostAt(5))
  vi.mocked(saveRawDraft).mockResolvedValue(wonAt(6))
  const saver = new DraftSaver(fromBase, clientAnswering(doc('base', 'a chunk'), 5))
  const shown = vi.fn()
  saver.subscribeToNewWords(shown)

  saver.edit(doc('base', 'my words'))
  saver.flush()
  await vi.waitFor(() => expect(saver.getSnapshot().status.kind).toBe('saved'))

  const [lost, merge] = vi.mocked(saveRawDraft).mock.calls.map(([call]) => call.body)
  expect(saveRawDraft).toHaveBeenCalledTimes(2)
  expect(merge).toMatchObject({ text: doc('base', 'a chunk', 'my words'), baseRevision: 5 })
  expect(merge?.savedAt).not.toBe(lost?.savedAt)
  expect(shown).toHaveBeenCalledWith(doc('base', 'a chunk', 'my words'), 'mergedIntoNewest')
  expect(saver.getSnapshot()).toMatchObject({ baseRevision: 6, conflict: null })
  saver.stop()
})

test('a loss, then a delete of the entry on another device: the read finds no entry, and the words go to a new entry', async () => {
  vi.mocked(saveRawDraft).mockResolvedValueOnce(lostAt(5))
  vi.mocked(saveRawDraft).mockResolvedValue(wonAt(1))
  vi.mocked(addSession).mockResolvedValue({ ...entryWithWords('', 0), id: 'session-2' })
  const queryClient = new QueryClient()
  vi.mocked(getSession).mockRejectedValue(new ProblemDetailsError({ type: 'about:blank', title: 'Gone', status: 404 }))
  const saver = new DraftSaver(fromBase, queryClient)

  saver.edit(doc('base', 'my words'))
  saver.flush()
  await vi.waitFor(() => expect(saveRawDraft).toHaveBeenCalledTimes(2))
  await vi.waitFor(() => expect(saver.getSnapshot().status.kind).toBe('saved'))

  expect(addSession).toHaveBeenCalledOnce()
  expect(vi.mocked(saveRawDraft).mock.lastCall?.[0]).toMatchObject({ path: { id: 'session-2' }, body: { text: doc('base', 'my words'), baseRevision: 0 } })
  expect(saver.getSnapshot()).toMatchObject({ sessionId: 'session-2', movedFromDeletedEntry: true, waitsForAnotherWriter: false })
  saver.stop()
})

test('a paragraph that the person changed and another device kept is not added a second time', async () => {
  vi.mocked(saveRawDraft).mockResolvedValueOnce(lostAt(5))
  vi.mocked(saveRawDraft).mockResolvedValue(wonAt(6))
  const saver = new DraftSaver(fromBase, clientAnswering(doc('base', 'OTHERDEVICE', 'CHUNK'), 5))

  saver.edit(doc('base word1'))
  saver.flush()
  await vi.waitFor(() => expect(saveRawDraft).toHaveBeenCalledTimes(2))

  expect(plainWordsOf(vi.mocked(saveRawDraft).mock.lastCall?.[0].body.text ?? '')).toBe('base word1\nOTHERDEVICE\nCHUNK')
  saver.stop()
})

test('after a loss, the words stay in the key of the tab until a save of the merge answers', async () => {
  vi.mocked(saveRawDraft).mockResolvedValueOnce(lostAt(5))
  const queryClient = new QueryClient()
  vi.mocked(getSession).mockReturnValue(new Promise(() => undefined))
  const saver = new DraftSaver(fromBase, queryClient)

  saver.edit(doc('base', 'my words'))
  saver.flush()
  await vi.waitFor(() => expect(getSession).toHaveBeenCalledOnce())

  expect(wordsInKeys()).toEqual([doc('base', 'my words')])
  expect(saver.getSnapshot().conflict).toBeNull()
  saver.stop()
})

test('after 3 losses in a row, the next read waits from 1 s, the wait doubles, and the page says that the words wait to save', async () => {
  vi.useFakeTimers()
  vi.spyOn(Math, 'random').mockReturnValue(0)
  // Five losses, then a win: with no wait, all six saves go at once, and the test fails and ends.
  const save = vi.mocked(saveRawDraft)
  for (let loss = 0; loss < 5; loss += 1) save.mockResolvedValueOnce(lostWithNoNumber())
  save.mockResolvedValue(wonAt(8))
  const saver = new DraftSaver(fromBase, clientAnswering(doc('base', 'newer words'), 7))

  saver.edit(doc('base', 'my words'))
  saver.flush()
  await vi.advanceTimersByTimeAsync(50)
  expect(saveRawDraft).toHaveBeenCalledTimes(3)
  expect(shownStatusOf(saver.getSnapshot()).kind).toBe('waitingForAnotherWriter')
  await vi.advanceTimersByTimeAsync(900)
  expect(saveRawDraft).toHaveBeenCalledTimes(3)
  await vi.advanceTimersByTimeAsync(100)
  expect(saveRawDraft).toHaveBeenCalledTimes(4)
  await vi.advanceTimersByTimeAsync(1900)
  expect(saveRawDraft).toHaveBeenCalledTimes(4)
  await vi.advanceTimersByTimeAsync(150)
  expect(saveRawDraft).toHaveBeenCalledTimes(5)

  const stamps = vi.mocked(saveRawDraft).mock.calls.map(([call]) => call.body.savedAt)
  expect(new Set(stamps).size).toBe(stamps.length)
  expect(wordsInKeys()).toEqual([doc('base', 'newer words', 'my words')])
  saver.stop()
  vi.useRealTimers()
})

test('a won save ends the waits, so a next loss reads at once', async () => {
  vi.useFakeTimers()
  vi.spyOn(Math, 'random').mockReturnValue(0)
  const save = vi.mocked(saveRawDraft)
  save.mockResolvedValueOnce(lostWithNoNumber()).mockResolvedValueOnce(lostWithNoNumber()).mockResolvedValueOnce(lostWithNoNumber())
  save.mockResolvedValueOnce(wonAt(8)).mockResolvedValueOnce(lostWithNoNumber()).mockResolvedValue(wonAt(9))
  const saver = new DraftSaver(fromBase, clientAnswering(doc('base', 'newer words'), 7))
  saver.subscribe(() => undefined)

  saver.edit(doc('base', 'my words'))
  saver.flush()
  await vi.advanceTimersByTimeAsync(1600)
  expect(save).toHaveBeenCalledTimes(4)
  expect(shownStatusOf(saver.getSnapshot()).kind).toBe('saved')

  saver.edit(doc('base', 'newer words', 'my words', 'and more'))
  saver.flush()
  await vi.advanceTimersByTimeAsync(50)
  expect(save).toHaveBeenCalledTimes(6)
  saver.stop()
  vi.useRealTimers()
})

test('a loss with no page merges the words and saves them, and a notice says that they joined the entry', async () => {
  vi.mocked(saveRawDraft).mockResolvedValueOnce(lostAt(5))
  vi.mocked(saveRawDraft).mockResolvedValue(wonAt(6))
  const saver = new DraftSaver(fromBase, clientAnswering(doc('base', 'a chunk'), 5))
  saver.edit(doc('base', 'words of a stale tab'))
  saver.flush()

  await vi.waitFor(() => expect(saver.notice()).toMatchObject({ kind: 'joined', sessionId: 'session-1' }))
  expect(vi.mocked(saveRawDraft).mock.lastCall?.[0].body.text).toBe(doc('base', 'a chunk', 'words of a stale tab'))
  saver.stop()
})

test('add my words after a Kept save left the Draft puts only the change of the person into the newest words', async () => {
  vi.mocked(saveRawDraft).mockResolvedValueOnce(keptAt(2))
  vi.mocked(saveRawDraft).mockResolvedValue(wonAt(8))
  const queryClient = new QueryClient()
  vi.mocked(getSession).mockResolvedValueOnce(entryWithWords(doc('base', 'OTHERDEVICE'), 7)).mockResolvedValue(entryWithWords(doc('base', 'OTHERDEVICE', 'CHUNK'), 8))
  const saver = new DraftSaver(fromBase, queryClient)
  saver.subscribe(() => undefined)
  saver.edit(doc('base word1'))
  saver.flush()
  await vi.waitFor(() => expect(saver.getSnapshot().conflict).toBe('wordsMovedToHistory'))

  const merged = await saver.keepWords(doc('base word1'))

  expect(plainWordsOf(merged ?? '')).toBe('base word1\nOTHERDEVICE\nCHUNK')
  await vi.waitFor(() => expect(saveRawDraft).toHaveBeenCalledTimes(2))
  expect(vi.mocked(saveRawDraft).mock.lastCall?.[0].body).toMatchObject({ text: merged, baseRevision: 8 })
  saver.stop()
})

test('add my words changes nothing when the read fails, so the words stay on the page and the notice stays', async () => {
  vi.mocked(saveRawDraft).mockResolvedValueOnce(keptAt(2))
  const queryClient = new QueryClient()
  const read = vi.mocked(getSession).mockResolvedValueOnce(entryWithWords('a chunk', 5))
  read.mockRejectedValue(new Error('Network Error'))
  const saver = new DraftSaver(target, queryClient)
  saver.subscribe(() => undefined)
  saver.edit('my words')
  saver.flush()
  await vi.waitFor(() => expect(saver.getSnapshot().conflict).toBe('wordsMovedToHistory'))

  await expect(saver.keepWords('my words')).resolves.toBeNull()

  expect(saver.getSnapshot()).toMatchObject({ conflict: 'wordsMovedToHistory', baseRevision: 1 })
  expect(saveRawDraft).toHaveBeenCalledOnce()
  saver.stop()
})

test('a replay of the create leaves the base, so a chunk in the row stays in the Draft', async () => {
  vi.mocked(addSession).mockResolvedValue({ ...entryWithWords('a chunk', 4), id: 'session-1' })
  vi.mocked(saveRawDraft).mockReturnValue(new Promise(() => undefined))
  const saver = new DraftSaver({ ...target, sessionId: null, baseRevision: 0 }, new QueryClient())

  saver.edit('words of a tab with no row yet')
  saver.flush()

  await vi.waitFor(() => expect(saveRawDraft).toHaveBeenCalledOnce())
  expect(vi.mocked(saveRawDraft).mock.lastCall?.[0].body.baseRevision).toBe(0)
  saver.stop()
})

const keptAt = (revision: number) => ({ outcome: 'Kept', draftRevision: revision, latestRevision: revision + 3, savedAt: '2026-11-06T15:31:00+00:00', cleanupStatus: 'Clean', version: revision }) as never
const lostWithNoNumber = () => ({ outcome: 'Lost', draftRevision: null, latestRevision: 9, savedAt: '2026-11-06T15:31:00+00:00', cleanupStatus: 'Clean', version: 9 }) as never
const doc = (...lines: string[]) => JSON.stringify({ type: 'doc', content: lines.map((text) => ({ type: 'paragraph', content: [{ type: 'text', text }] })) })

test('a Kept number is not a base: the read decides the base, and the page says Saved only when the Draft holds the words', async () => {
  vi.mocked(saveRawDraft).mockResolvedValue(keptAt(2))
  const saver = new DraftSaver(target, clientAnswering('my words', 7))

  saver.edit('my words')
  saver.flush()
  await vi.waitFor(() => expect(saver.getSnapshot().status.kind).toBe('saved'))

  expect(saver.getSnapshot()).toMatchObject({ baseRevision: 7, conflict: null })
  saver.stop()
})

test('a Kept save whose words left the Draft tells the truth, and the base stays', async () => {
  vi.mocked(saveRawDraft).mockResolvedValue(keptAt(2))
  const saver = new DraftSaver(target, clientAnswering('newer words of another tab', 7))

  saver.edit('my words')
  saver.flush()
  await vi.waitFor(() => expect(saver.getSnapshot().conflict).toBe('wordsMovedToHistory'))

  expect(saver.getSnapshot().baseRevision).toBe(1)
  expect(shownStatusOf(saver.getSnapshot()).kind).toBe('savedInHistory')
  saver.stop()
})

test('a Lost save has no number in its reply, and the next save goes only after the read', async () => {
  vi.mocked(saveRawDraft).mockResolvedValueOnce(lostWithNoNumber())
  vi.mocked(saveRawDraft).mockReturnValue(new Promise(() => undefined))
  const queryClient = new QueryClient()
  let answer: (session: SessionDto) => void = () => undefined
  vi.mocked(getSession).mockImplementation(() => new Promise((resolve) => { answer = resolve as typeof answer }))
  const saver = new DraftSaver(target, queryClient)
  saver.subscribe(() => undefined)

  saver.edit('my words')
  saver.flush()
  await vi.waitFor(() => expect(getSession).toHaveBeenCalledOnce())
  saver.edit('my words and more')
  saver.flush()
  await new Promise((resolve) => setTimeout(resolve, 20))
  expect(saveRawDraft).toHaveBeenCalledOnce()

  answer(entryWithWords('newer words', 7))
  await vi.waitFor(() => expect(saveRawDraft).toHaveBeenCalledTimes(2))
  saver.stop()
})

test('a failed read after a loss sends no save and raises no notice, and the read goes again', async () => {
  vi.useFakeTimers()
  vi.mocked(saveRawDraft).mockResolvedValueOnce(lostWithNoNumber())
  vi.mocked(saveRawDraft).mockResolvedValue(wonAt(8))
  const queryClient = new QueryClient()
  // A read goes one more time after 1 s, as the query of a Session does, so a failed read is two failures.
  const read = vi.mocked(getSession).mockRejectedValueOnce(new Error('Network Error')).mockRejectedValueOnce(new Error('Network Error'))
  read.mockResolvedValue(entryWithWords(doc('base', 'newer words'), 7))
  const saver = new DraftSaver(fromBase, queryClient)

  saver.edit(doc('base', 'my words'))
  saver.flush()
  await vi.advanceTimersByTimeAsync(1010)
  expect(saveRawDraft).toHaveBeenCalledOnce()
  expect(saver.getSnapshot().conflict).toBeNull()
  expect(saver.getSnapshot().status.kind).toBe('waiting')

  await vi.advanceTimersByTimeAsync(3000)
  expect(read).toHaveBeenCalledTimes(3)
  expect(saveRawDraft).toHaveBeenCalledTimes(2)
  expect(vi.mocked(saveRawDraft).mock.lastCall?.[0].body).toMatchObject({ text: doc('base', 'newer words', 'my words'), baseRevision: 7 })
  saver.stop()
  vi.useRealTimers()
})

test('a refused read after a loss is no refusal of the words, so the read goes again', async () => {
  vi.useFakeTimers()
  vi.mocked(saveRawDraft).mockResolvedValueOnce(lostWithNoNumber())
  vi.mocked(saveRawDraft).mockResolvedValue(wonAt(8))
  const queryClient = new QueryClient()
  const read = vi.mocked(getSession).mockRejectedValueOnce(new ProblemDetailsError({ type: 'about:blank', title: 'Not now.', status: 422 }))
  read.mockResolvedValue(entryWithWords(doc('base', 'newer words'), 7))
  const saver = new DraftSaver(fromBase, queryClient)

  saver.edit(doc('base', 'my words'))
  saver.flush()
  await vi.advanceTimersByTimeAsync(10)
  expect(saver.getSnapshot().status.kind).toBe('waiting')

  await vi.advanceTimersByTimeAsync(3000)
  expect(saveRawDraft).toHaveBeenCalledTimes(2)
  expect(vi.mocked(saveRawDraft).mock.lastCall?.[0].body.baseRevision).toBe(7)
  saver.stop()
  vi.useRealTimers()
})

test('words that the Draft holds raise no notice and send no save, and the page takes the Draft', async () => {
  vi.mocked(saveRawDraft).mockResolvedValueOnce(lostWithNoNumber())
  const saver = new DraftSaver(fromBase, clientAnswering(doc('a chunk', 'base', 'my words'), 7))
  const shown = vi.fn()
  saver.subscribeToNewWords(shown)
  saver.subscribe(() => undefined)

  saver.edit(doc('base', 'my words'))
  saver.flush()
  await vi.waitFor(() => expect(saver.getSnapshot().status.kind).toBe('saved'))

  expect(saver.getSnapshot()).toMatchObject({ conflict: null, baseRevision: 7 })
  expect(shown).toHaveBeenCalledWith(doc('a chunk', 'base', 'my words'), 'draftHoldsMore')
  expect(saveRawDraft).toHaveBeenCalledOnce()
  saver.stop()
})

test('words that the Draft holds with no page raise no notice of History', async () => {
  vi.mocked(saveRawDraft).mockResolvedValueOnce(lostWithNoNumber()).mockResolvedValue(wonAt(8))
  const saver = new DraftSaver(fromBase, clientAnswering(doc('a chunk', 'base', 'my words'), 7))

  saver.edit(doc('base', 'my words'))
  saver.flush()
  await vi.waitFor(() => expect(saver.getSnapshot().status.kind).toBe('saved'))

  expect(saver.notice()).toBeNull()
  saver.stop()
})

test('a person who types during the read keeps each word: the merge takes the newest typing', async () => {
  vi.mocked(saveRawDraft).mockResolvedValueOnce(lostWithNoNumber())
  vi.mocked(saveRawDraft).mockReturnValue(new Promise(() => undefined))
  const queryClient = new QueryClient()
  let answer: (session: SessionDto) => void = () => undefined
  vi.mocked(getSession).mockImplementation(() => new Promise((resolve) => { answer = resolve as typeof answer }))
  const saver = new DraftSaver(fromBase, queryClient)
  const shown = vi.fn()
  saver.subscribeToNewWords(shown)

  saver.edit(doc('base', 'my words'))
  saver.flush()
  await vi.waitFor(() => expect(getSession).toHaveBeenCalledOnce())
  saver.edit(doc('base', 'my words and a key'))
  answer(entryWithWords(doc('base', 'a chunk'), 7))
  await vi.waitFor(() => expect(saveRawDraft).toHaveBeenCalledTimes(2))

  const merged = doc('base', 'a chunk', 'my words and a key')
  expect(vi.mocked(saveRawDraft).mock.lastCall?.[0].body).toMatchObject({ text: merged, baseRevision: 7 })
  expect(shown).toHaveBeenCalledWith(merged, 'mergedIntoNewest')
  saver.stop()
})

test('a Kept state keeps its notice after a reload, and the words open on the page', async () => {
  vi.mocked(saveRawDraft).mockResolvedValue(keptAt(2))
  const before = new DraftSaver(target, clientAnswering('a chunk', 7))
  before.edit('my words')
  before.flush()
  await vi.waitFor(() => expect(before.getSnapshot().conflict).toBe('wordsMovedToHistory'))
  const [kept] = readOutbox(owner)
  expect(kept?.entry).toMatchObject({ change: null, wordsInHistory: 'my words', baseRevision: 1 })
  before.stop()
  // The tab closes, and the next tab takes its key.
  closeEntryLocks()
  removeEachKey()
  storedByAnotherTab(kept?.entry ?? entryWith('no key'))

  const after = new DraftSaver({ ...target, baseRevision: 1 }, new QueryClient())

  expect(after.getSnapshot().conflict).toBe('wordsMovedToHistory')
  expect(after.wordsToOpen()).toBe('my words')
  expect(after.notice()).toMatchObject({ kind: 'inHistory', words: 'my words' })
  after.dismissConflict()
  expect(readOutbox(owner)).toEqual([])
  after.stop()
})

test('a notice of History offers no discard and no word of a sign-out, because the Server holds the words', async () => {
  vi.mocked(saveRawDraft).mockResolvedValue(keptAt(2))
  const saver = new DraftSaver(target, clientAnswering('a chunk', 7))
  saver.edit('my words')
  saver.flush()
  await vi.waitFor(() => expect(saver.getSnapshot().conflict).toBe('wordsMovedToHistory'))

  expect(unsentWordsOnThisDevice(owner, []).map(({ words }) => words)).not.toContain('my words')
  saver.stop()
  removeNoticesOfWordsInHistory(owner)
  expect(readOutbox(owner)).toEqual([])
})

test('words of a tab that closed offline return with a notice when they reach the Draft with no page', async () => {
  vi.mocked(saveRawDraft).mockResolvedValue(wonAt(2))
  storedByAnotherTab(entryWith('words of a closed tab'))

  const saver = new DraftSaver(target, new QueryClient())

  await vi.waitFor(() => expect(saver.notice()).toMatchObject({ kind: 'joined', sessionId: 'session-1' }))
  saver.stop()
})

test('words of a tab that closed raise no notice when an open page shows them', async () => {
  vi.mocked(saveRawDraft).mockResolvedValue(wonAt(2))
  storedByAnotherTab(entryWith('words of a closed tab'))

  const saver = new DraftSaver(target, new QueryClient())
  const leavePage = saver.subscribe(() => undefined)

  await vi.waitFor(() => expect(saver.getSnapshot().status.kind).toBe('saved'))
  expect(saver.notice()).toBeNull()
  leavePage()
  expect(saver.notice()).toBeNull()
  saver.stop()
})

test('held words whose save loses go into the newest Draft by a merge, and the notice says that they joined the entry', async () => {
  vi.mocked(saveRawDraft).mockResolvedValueOnce(lostWithNoNumber())
  vi.mocked(saveRawDraft).mockResolvedValue(wonAt(8))
  storedByAnotherTab(entryWith('', { change: null, held: 'words of a hold' }))

  const saver = new DraftSaver(target, clientAnswering('other words of another hold', 7))

  await vi.waitFor(() => expect(saver.notice()).toMatchObject({ kind: 'joined' }))
  expect(plainWordsOf(vi.mocked(saveRawDraft).mock.lastCall?.[0].body.text ?? '')).toContain('words of a hold')
  expect(vi.mocked(saveRawDraft).mock.lastCall?.[0].body.baseRevision).toBe(7)
  saver.stop()
})

test('a block that the entry already holds does not show twice when a hold joins it', () => {
  vi.mocked(saveRawDraft).mockReturnValue(new Promise(() => undefined))
  const saver = aSaver()
  saver.holdInOutbox(doc('held words'))

  expect(plainWordsOf(saver.joinHoldInto(doc('words of the entry', 'held words')))).toBe('words of the entry\nheld words')
  saver.stop()
})

const removeEachKey = () => {
  for (const { storageKey } of readOutbox(owner)) localStorage.removeItem(storageKey)
}

test('a discarded word does not go out from a tab that wakes, and the page says that the words are only there', async () => {
  vi.useFakeTimers()
  vi.mocked(saveRawDraft).mockRejectedValueOnce(new Error('Network Error'))
  vi.mocked(saveRawDraft).mockResolvedValue(wonAt(2))
  const saver = new DraftSaver(target, clientAnswering('the Draft', 1))
  saver.subscribe(() => undefined)
  saver.edit('a word that another tab discards')
  saver.flush()
  await vi.advanceTimersByTimeAsync(10)

  removeEachKey()
  await vi.advanceTimersByTimeAsync(3000)

  expect(saveRawDraft).toHaveBeenCalledOnce()
  expect(saver.getSnapshot().conflict).toBe('discardedInAnotherTab')
  expect(shownStatusOf(saver.getSnapshot()).kind).toBe('idle')
  expect(readOutbox(owner)).toEqual([])
  saver.stop()
  vi.useRealTimers()
})

test('with no page, a discarded word goes with no notice', async () => {
  vi.useFakeTimers()
  vi.mocked(saveRawDraft).mockRejectedValueOnce(new Error('Network Error'))
  const saver = new DraftSaver(target, clientAnswering('the Draft', 1))
  saver.edit('a word that another tab discards')
  saver.flush()
  await vi.advanceTimersByTimeAsync(10)

  removeEachKey()
  await vi.advanceTimersByTimeAsync(3000)

  expect(saveRawDraft).toHaveBeenCalledOnce()
  expect(saver.notice()).toBeNull()
  saver.stop()
  vi.useRealTimers()
})

test('a tab that wakes does not save later words of a key that is gone', async () => {
  vi.useFakeTimers()
  vi.mocked(saveRawDraft).mockRejectedValueOnce(new Error('Network Error'))
  const saver = new DraftSaver(target, clientAnswering('the Draft', 1))
  saver.subscribe(() => undefined)
  saver.edit('first words')
  saver.flush()
  await vi.advanceTimersByTimeAsync(10)
  saver.edit('first words and more')

  removeEachKey()
  await vi.advanceTimersByTimeAsync(10_000)

  expect(saveRawDraft).toHaveBeenCalledOnce()
  expect(readOutbox(owner)).toEqual([])
  expect(saver.getSnapshot().conflict).toBe('discardedInAnotherTab')
  saver.stop()
  vi.useRealTimers()
})

test('a tab that wakes on an entry that is gone says that the words may be only on the page, and no new entry is promised', async () => {
  vi.useFakeTimers()
  vi.mocked(saveRawDraft).mockRejectedValueOnce(new Error('Network Error'))
  const queryClient = new QueryClient()
  vi.mocked(getSession).mockRejectedValue(new ProblemDetailsError({ type: 'about:blank', title: 'Gone', status: 404 }))
  const saver = new DraftSaver(target, queryClient)
  saver.subscribe(() => undefined)
  saver.edit('my words')
  saver.flush()
  await vi.advanceTimersByTimeAsync(10)

  removeEachKey()
  await vi.advanceTimersByTimeAsync(10_000)

  expect(saver.getSnapshot()).toMatchObject({ movedFromDeletedEntry: false, conflict: 'discardedInAnotherTab' })
  expect(addSession).not.toHaveBeenCalled()
  saver.stop()
  vi.useRealTimers()
})

test('with no entry yet, both answers of a wake end the notice: mine saves the words, and newest empties the page', async () => {
  vi.useFakeTimers()
  vi.mocked(addSession).mockRejectedValueOnce(new Error('Network Error'))
  const newTarget = { ...target, sessionId: null, baseRevision: 0 }
  const queryClient = new QueryClient()
  const one = new DraftSaver(newTarget, queryClient)
  one.subscribe(() => undefined)
  one.edit('my words')
  one.flush()
  await vi.advanceTimersByTimeAsync(10)
  removeEachKey()
  await vi.advanceTimersByTimeAsync(10_000)
  expect(one.getSnapshot().conflict).toBe('discardedInAnotherTab')

  await expect(one.takeNewestWords()).resolves.toBe('')
  expect(one.getSnapshot().conflict).toBeNull()
  one.stop()

  vi.mocked(addSession).mockRejectedValueOnce(new Error('Network Error'))
  const two = new DraftSaver({ ...newTarget, clientSessionId: 'another-entry' }, queryClient)
  two.subscribe(() => undefined)
  two.edit('my words')
  two.flush()
  await vi.advanceTimersByTimeAsync(10)
  removeEachKey()
  await vi.advanceTimersByTimeAsync(10_000)
  vi.mocked(addSession).mockReturnValue(new Promise(() => undefined))
  const calls = vi.mocked(addSession).mock.calls.length

  await expect(two.keepWords('my words')).resolves.toBe('my words')
  await vi.advanceTimersByTimeAsync(10)

  expect(two.getSnapshot().conflict).toBeNull()
  expect(vi.mocked(addSession).mock.calls.length).toBeGreaterThan(calls)
  two.stop()
  vi.useRealTimers()
})

test('a read after a save stops an older read that still runs, so it does not take the older answer', async () => {
  vi.mocked(saveRawDraft).mockResolvedValueOnce(lostWithNoNumber()).mockResolvedValue(wonAt(8))
  const queryClient = new QueryClient()
  const order: string[] = []
  vi.spyOn(queryClient, 'cancelQueries').mockImplementation(() => {
    order.push('cancel')
    return Promise.resolve()
  })
  vi.mocked(getSession).mockImplementation(() => {
    order.push('read')
    return Promise.resolve(entryWithWords('newer words', 7))
  })
  const saver = new DraftSaver(target, queryClient)

  saver.edit('my words')
  saver.flush()
  await vi.waitFor(() => expect(order).toContain('read'))

  expect(order.slice(0, 2)).toEqual(['cancel', 'read'])
  saver.stop()
})

test('a read after a save takes the answer of the server, also when another read stops it, and not the older words in the cache', async () => {
  vi.mocked(saveRawDraft).mockResolvedValue(keptAt(2))
  const queryClient = new QueryClient()
  queryClient.setQueryData(SessionKeys.detail('session-1'), entryWithWords('', 0))
  let answer: (session: SessionDto) => void = () => undefined
  vi.mocked(getSession).mockImplementation(() => new Promise((resolve) => { answer = resolve as typeof answer }))
  const saver = new DraftSaver(target, queryClient)

  saver.edit('my words')
  saver.flush()
  await vi.waitFor(() => expect(getSession).toHaveBeenCalledOnce())
  // A poll or the read of another saver of this tab stops each read of the Session in the cache.
  await queryClient.cancelQueries()
  answer(entryWithWords('my words', 7))

  await vi.waitFor(() => expect(saver.getSnapshot().status.kind).toBe('saved'))
  expect(saver.getSnapshot()).toMatchObject({ baseRevision: 7, conflict: null })
  saver.stop()
})

test('a Kept save whose Draft holds more takes the Draft, also when the person typed and then deleted the typing, so no word of another device goes out', async () => {
  vi.mocked(saveRawDraft).mockResolvedValueOnce(keptAt(2)).mockResolvedValue(wonAt(8))
  let answer: (session: SessionDto) => void = () => undefined
  vi.mocked(getSession).mockImplementation(() => new Promise((resolve) => { answer = resolve as typeof answer }))
  const saver = new DraftSaver(target, new QueryClient())

  saver.edit('my words')
  saver.flush()
  await vi.waitFor(() => expect(getSession).toHaveBeenCalledOnce())
  saver.edit('my words and more')
  saver.edit('my words')
  answer(entryWithWords('my words, words of another device', 7))

  await vi.waitFor(() => expect(saveRawDraft).toHaveBeenCalledTimes(2))
  const body = vi.mocked(saveRawDraft).mock.lastCall?.[0].body
  expect(plainWordsOf(body?.text ?? '')).toContain('words of another device')
  expect(body?.baseRevision).toBe(7)
  saver.stop()
})

test('a tab that wakes after another tab saved its key raises no notice', async () => {
  vi.useFakeTimers()
  vi.mocked(saveRawDraft).mockRejectedValueOnce(new Error('Network Error'))
  const saver = new DraftSaver(target, clientAnswering('words that went', 5))
  saver.subscribe(() => undefined)
  saver.edit('words that went')
  saver.flush()
  await vi.advanceTimersByTimeAsync(10)

  removeEachKey()
  await vi.advanceTimersByTimeAsync(3000)

  expect(saver.getSnapshot()).toMatchObject({ conflict: null, baseRevision: 5 })
  saver.stop()
  vi.useRealTimers()
})

test('a key typed after the wake goes out as new text, which the page shows', async () => {
  vi.useFakeTimers()
  vi.mocked(saveRawDraft).mockRejectedValueOnce(new Error('Network Error'))
  vi.mocked(saveRawDraft).mockResolvedValue(wonAt(2))
  const saver = new DraftSaver(target, clientAnswering('the Draft', 1))
  saver.edit('a word')
  saver.flush()
  await vi.advanceTimersByTimeAsync(10)

  removeEachKey()
  saver.edit('a word and a key')
  await vi.advanceTimersByTimeAsync(4000)

  expect(vi.mocked(saveRawDraft).mock.lastCall?.[0].body.text).toBe('a word and a key')
  saver.stop()
  vi.useRealTimers()
})

test.each([
  [1, 0, 0],
  [2, 1, 0],
  [3, 0, 1000],
  [3, 1, 1500],
  [4, 0, 2000],
  [7, 0.5, 20_000],
  [8, 0, 30_000],
  [40, 1, 45_000],
])('after %i losses in a row with a random part of %f, the wait is %i ms', (lostInARow, jitter, ms) => {
  expect(waitAfterLosses(lostInARow, jitter)).toBe(ms)
})

const markOfAnotherTab = () => localStorage.setItem(`recall.editor.recall.edit.${owner}.${target.clientSessionId}`, 'another-tab')

test('a tab that takes an entry after a crash sends the change in flight first with its own stamp, and then the newest change, from the base of that tab', async () => {
  vi.mocked(saveRawDraft).mockResolvedValueOnce(wonAt(2)).mockResolvedValue(wonAt(3))
  const sent = { value: doc('base', 'sent words'), savedAt: '2026-11-06T15:31:00.000Z' }
  storedByAnotherTab(entryWith('', { baseText: doc('base'), sent, change: { value: doc('base', 'sent words and more'), savedAt: '2026-11-06T15:32:00.000Z' } }))

  const saver = new DraftSaver(target, new QueryClient())
  saver.subscribe(() => undefined)
  await vi.waitFor(() => expect(saveRawDraft).toHaveBeenCalledTimes(2))

  const [first, second] = vi.mocked(saveRawDraft).mock.calls.map(([call]) => call.body)
  expect(first).toMatchObject({ text: sent.value, savedAt: sent.savedAt, baseRevision: 1 })
  expect(second).toMatchObject({ text: doc('base', 'sent words and more'), baseRevision: 2 })
  expect(saver.getSnapshot().editing).toBe('here')
  saver.stop()
})

test('a tab that takes an entry after a crash merges a loss from the base of that tab', async () => {
  vi.mocked(saveRawDraft).mockResolvedValueOnce(lostAt(5)).mockResolvedValue(wonAt(6))
  storedByAnotherTab(entryWith('', { baseText: doc('base'), change: { value: doc('base', 'words of a closed tab'), savedAt: '2026-11-06T15:31:00.000Z' } }))

  const saver = new DraftSaver(target, clientAnswering(doc('base', 'a chunk'), 5))
  await vi.waitFor(() => expect(saveRawDraft).toHaveBeenCalledTimes(2))

  expect(vi.mocked(saveRawDraft).mock.lastCall?.[0].body).toMatchObject({ text: doc('base', 'a chunk', 'words of a closed tab'), baseRevision: 5 })
  saver.stop()
})

test('a key of an older version with no base text reads Revision baseRevision before its first save, and a loss merges from that text', async () => {
  vi.mocked(getRawRevision).mockResolvedValue({ number: 1, savedAt: '2026-11-06T15:30:30+00:00', text: doc('base') })
  vi.mocked(saveRawDraft).mockResolvedValueOnce(lostAt(5)).mockResolvedValue(wonAt(6))
  localStorage.setItem(`recall.unsent.${owner}.another-tab.1`, JSON.stringify({ ...entryWith(doc('words of an older version')), baseText: undefined }))

  const saver = new DraftSaver(target, clientAnswering(doc('base', 'a chunk'), 5))
  await vi.waitFor(() => expect(saveRawDraft).toHaveBeenCalledTimes(2))

  expect(getRawRevision).toHaveBeenCalledWith(expect.objectContaining({ path: { id: 'session-1', number: 1 } }))
  expect(vi.mocked(getRawRevision).mock.invocationCallOrder[0]).toBeLessThan(vi.mocked(saveRawDraft).mock.invocationCallOrder[0])
  expect(vi.mocked(saveRawDraft).mock.lastCall?.[0].body).toMatchObject({ text: doc('words of an older version', 'a chunk'), baseRevision: 5 })
  saver.stop()
})

test('a key with no base text whose Revision read fails keeps its words in the key and sends nothing', async () => {
  vi.mocked(getRawRevision).mockRejectedValue(new TypeError('Failed to fetch'))
  localStorage.setItem(`recall.unsent.${owner}.another-tab.1`, JSON.stringify({ ...entryWith('words of an older version'), baseText: undefined }))

  const saver = new DraftSaver(target, new QueryClient())
  await vi.waitFor(() => expect(getRawRevision).toHaveBeenCalled())

  expect(saveRawDraft).not.toHaveBeenCalled()
  expect(wordsInKeys()).toEqual(['words of an older version'])
  expect(saver.getSnapshot().status.kind).not.toBe('saved')
  saver.stop()
})

test('a tab that took an entry before its read answered reads the Draft before its first save', async () => {
  vi.mocked(saveRawDraft).mockResolvedValue(wonAt(6))
  const read = clientAnswering(doc('base', 'a chunk'), 5)
  storedByAnotherTab(entryWith('', { baseText: doc('base'), readsFirst: true, change: { value: doc('base', 'my words'), savedAt: '2026-11-06T15:31:00.000Z' } }))

  const saver = new DraftSaver(target, read)
  saver.flush()
  await vi.waitFor(() => expect(saveRawDraft).toHaveBeenCalledOnce())

  expect(getSession).toHaveBeenCalled()
  expect(vi.mocked(saveRawDraft).mock.lastCall?.[0].body).toMatchObject({ text: doc('base', 'a chunk', 'my words'), baseRevision: 5 })
  saver.stop()
})

test('a tab that loses the entry hands over its words, its base, and its key, and then writes and sends nothing', () => {
  vi.mocked(saveRawDraft).mockReturnValue(new Promise(() => undefined))
  const saver = new DraftSaver(fromBase, new QueryClient())
  saver.edit(doc('base', 'my words'))
  const [{ storageKey } = { storageKey: '' }] = readOutbox(owner)

  const handed = saver.handOver()
  saver.edit(doc('base', 'my words after the loss'))
  saver.flush()

  expect(handed).toMatchObject({ storageKey, entry: { baseText: fromBase.baseText, change: { value: doc('base', 'my words') } } })
  expect(wordsInKeys()).toEqual([doc('base', 'my words')])
  expect(saveRawDraft).not.toHaveBeenCalled()
  expect(saver.getSnapshot().editing).toBe('elsewhere')
  saver.stop()
})

test('a tab sees the mark of another tab before it writes, so it goes read-only and keeps no new word', () => {
  vi.mocked(saveRawDraft).mockReturnValue(new Promise(() => undefined))
  const saver = aSaver()
  const shown = vi.fn()
  saver.subscribe(shown)
  saver.edit('my words')

  markOfAnotherTab()
  saver.edit('my words and a key of a stale tab')

  expect(wordsInKeys()).toEqual(['my words'])
  expect(saver.getSnapshot().editing).toBe('elsewhere')
  expect(shown).toHaveBeenCalled()
  saver.stop()
})

test('a page that waits for the entry shows the words that the tab that edits holds in its key', () => {
  markOfAnotherTab()
  storedByAnotherTab(entryWith('words of the tab that edits'))

  const saver = aSaver()

  expect(saver.getSnapshot()).toMatchObject({ editing: 'elsewhere', wordsOfTheEditor: 'words of the tab that edits' })
  saver.edit('a key in a read-only page')
  expect(wordsInKeys()).toEqual(['words of the tab that edits'])
  saver.stop()
})

test('a page that handed its key over shows the words in that key, as the words of the tab that edits now', () => {
  markOfAnotherTab()
  // A taker with no room keeps the handed key, so the key still has the name of this tab.
  localStorage.setItem(`recall.unsent.${owner}.${thisTab}.99`, JSON.stringify(entryWith('words the other tab took')))

  const saver = aSaver()

  expect(saver.getSnapshot()).toMatchObject({ editing: 'elsewhere', wordsOfTheEditor: 'words the other tab took' })
  saver.stop()
})

test('words that came in a hand-over go on from their own key, and a late copy of the same state goes once', async () => {
  vi.mocked(saveRawDraft).mockReturnValue(new Promise(() => undefined))
  const saver = aSaver()
  const entry = entryWith('handed words', { stateId: 'another-tab.3' })
  const handed = { from: 'another-tab', drafts: [{ storageKey: null, entry }] }

  saver.takeOver(handed)
  saver.takeLateHandOver(handed)
  saver.flush()
  await vi.waitFor(() => expect(saveRawDraft).toHaveBeenCalledOnce())

  expect(saver.unsettledText()).toBe('handed words')
  expect(wordsInKeys()).toEqual(['handed words'])
  await new Promise((resolve) => setTimeout(resolve, 20))
  expect(saveRawDraft).toHaveBeenCalledOnce()
  saver.stop()
})

test('a late hand-over of words that this tab does not hold goes with its own base beside the page', async () => {
  vi.mocked(saveRawDraft).mockReturnValue(new Promise(() => undefined))
  const saver = aSaver()
  saver.edit('words of this tab')
  const late = entryWith('words of a frozen tab', { stateId: 'another-tab.7', baseText: doc('base'), baseRevision: 3 })

  saver.takeLateHandOver({ from: 'another-tab', drafts: [{ storageKey: null, entry: late }] })
  saver.flush()
  await vi.waitFor(() => expect(saveRawDraft).toHaveBeenCalledTimes(2))

  const bodies = vi.mocked(saveRawDraft).mock.calls.map(([call]) => call.body)
  expect(bodies).toEqual(expect.arrayContaining([expect.objectContaining({ text: 'words of a frozen tab', baseRevision: 3 }), expect.objectContaining({ text: 'words of this tab' })]))
  expect([...wordsInKeys()].sort()).toEqual(['words of a frozen tab', 'words of this tab'])
  saver.stop()
})
