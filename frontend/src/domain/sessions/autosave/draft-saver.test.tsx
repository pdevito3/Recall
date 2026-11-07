import { QueryClient } from '@tanstack/react-query'
import { afterEach, expect, test, vi } from 'vitest'

import { addSession } from '@/api/generated/sessions/clients/add-session'
import type { SessionDto } from '@/api/generated/models/types/session-dto'
import { saveRawDraft } from '@/api/generated/sessions/clients/save-raw-draft'
import { ProblemDetailsError } from '@/lib/problem-details'

import { plainWordsOf } from '../editor/content'
import { DraftSaver, draftNotices, draftSaverFor, removeNoticesOfWordsInHistory, shownStatusOf, unsentWordsOnThisDevice, type DraftTarget } from './draft-saver'
import { readOutbox, writeToOutbox, type OutboxEntry, type StoredEntry } from './outbox'

vi.mock('@/api/generated/sessions/clients/add-session')
vi.mock('@/api/generated/sessions/clients/save-raw-draft')

const owner = 'person-a'
const target: DraftTarget = {
  owner,
  channel: 'raw',
  clientSessionId: 'client-session',
  startedAt: '2026-11-06T15:30:00.000Z',
  sessionId: 'session-1',
  baseRevision: 1,
}

const aSaver = () => new DraftSaver(target, new QueryClient())

afterEach(() => {
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
  vi.spyOn(queryClient, 'fetchQuery').mockResolvedValue(entryWithWords(rawText, revision))
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

  const saver = new DraftSaver(target, new QueryClient(), stored)

  expect(localStorage.getItem(stored.storageKey)).toBeNull()
  expect(wordsInKeys()).toEqual(['words of a closed tab'])
  saver.stop()
})

test('a taken text on a full storage stays in the key of the tab that left', () => {
  vi.mocked(saveRawDraft).mockReturnValue(new Promise(() => undefined))
  const stored = storedByAnotherTab(entryWith('words of a closed tab'))
  const setItem = vi.spyOn(Storage.prototype, 'setItem')
  setItem.mockImplementationOnce(() => {
    throw new DOMException('full', 'QuotaExceededError')
  })

  const saver = new DraftSaver(target, new QueryClient(), stored)

  expect(wordsInKeys()).toEqual(['words of a closed tab'])
  saver.stop()
})

test('a page that opened before a tab left shows the words of that tab, with their stamp', () => {
  vi.mocked(saveRawDraft).mockReturnValue(new Promise(() => undefined))
  const saver = aSaver()
  const shown = vi.fn()
  saver.subscribeToNewWords(shown)
  const stored = storedByAnotherTab(entryWith('words of a crashed tab', { baseRevision: 3 }))

  expect(saver.takeEntryIfUntouched(stored)).toBe(true)

  expect(shown).toHaveBeenCalledWith('words of a crashed tab', 'heldWordsJoined')
  expect(saver.getSnapshot().baseRevision).toBe(3)
  expect(saver.unsettledText()).toBe('words of a crashed tab')
  expect(readOutbox(owner)[0]?.entry.change?.savedAt).toBe('2026-11-06T15:31:00.000Z')
  expect(localStorage.getItem(stored.storageKey)).toBeNull()
  saver.stop()
})

test('a page that the person touched does not take the words of another tab', () => {
  vi.mocked(saveRawDraft).mockReturnValue(new Promise(() => undefined))
  const saver = aSaver()
  saver.edit('words of this page')
  const stored = storedByAnotherTab(entryWith('words of a crashed tab'))

  expect(saver.takeEntryIfUntouched(stored)).toBe(false)

  expect(localStorage.getItem(stored.storageKey)).not.toBeNull()
  expect(saver.unsettledText()).toBe('words of this page')
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
  const stored = storedByAnotherTab(entryWith('', { change: null, held: 'words of an earlier sign-in' }))

  const saver = new DraftSaver(target, clientAnswering('words on the server'), stored)

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

test('a Lost save on a full storage shows the words in History, and no notice with no words', async () => {
  fullStorage()
  vi.mocked(saveRawDraft).mockResolvedValue(lostWithNoNumber())
  const queryClient = new QueryClient()
  vi.spyOn(queryClient, 'fetchQuery').mockResolvedValue(entryWithWords('newer words', 7))
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

test('with no room at all, the older key stays and the person sees a notice', () => {
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

  expect(wordsInKeys()).toEqual(['words'])
  expect(saver.isHoldingWordsOnlyInMemory()).toBe(true)
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

test('a move to a new entry on a full storage keeps the saved words of the old entry', async () => {
  const stored = storedByAnotherTab(entryWith('saved words'))
  fullQuota()
  vi.mocked(saveRawDraft).mockRejectedValueOnce(new ProblemDetailsError({ type: 'about:blank', title: 'Gone', status: 404 }))
  vi.mocked(addSession).mockReturnValue(new Promise(() => undefined))

  const saver = new DraftSaver(target, new QueryClient(), stored)
  saver.flush()
  await vi.waitFor(() => expect(saver.getSnapshot().movedFromDeletedEntry).toBe(true))

  expect(wordsInKeys()).toEqual(['saved words'])
  saver.stop()
})

test('after a move on a full storage, a next write replaces the old copy, and a discard removes it', async () => {
  const stored = storedByAnotherTab(entryWith('saved words'))
  const setItem = fullQuota()
  vi.mocked(saveRawDraft).mockRejectedValueOnce(new ProblemDetailsError({ type: 'about:blank', title: 'Gone', status: 404 }))
  vi.mocked(addSession).mockReturnValue(new Promise(() => undefined))
  const saver = new DraftSaver(target, new QueryClient(), stored)
  saver.flush()
  await vi.waitFor(() => expect(saver.getSnapshot().movedFromDeletedEntry).toBe(true))
  setItem.mockRestore()

  saver.edit('saved words and more')
  expect(wordsInKeys()).toEqual(['saved words and more'])

  saver.discard()
  expect(readOutbox(owner)).toEqual([])
})

test('a key with words that the same draft key of this tab holds is removed, and no second notice comes', async () => {
  vi.mocked(saveRawDraft).mockReturnValue(new Promise(() => undefined))
  const saver = aSaver()
  saver.edit('the same words')
  const own = readOutbox(owner)[0]?.entry
  expect(own && saver.keepsTheSameWordsAs(own)).toBe(true)
  expect(saver.keepsTheSameWordsAs(entryWith('other words'))).toBe(false)
  saver.stop()
})

test('a loss keeps the base behind, so a next save cannot push a chunk of a recording out of the Draft', async () => {
  vi.mocked(saveRawDraft).mockResolvedValue(lostAt(5))
  const saver = new DraftSaver(target, clientAnswering('a chunk', 5))

  saver.edit('words from an old base')
  saver.flush()
  await vi.waitFor(() => expect(saver.getSnapshot().conflict).toBe('wordsMovedToHistory'))

  expect(saver.getSnapshot().baseRevision).toBe(1)
  saver.edit('more words from the old base')
  saver.flush()
  await vi.waitFor(() => expect(saveRawDraft).toHaveBeenCalledTimes(2))
  expect(vi.mocked(saveRawDraft).mock.lastCall?.[0].body.baseRevision).toBe(1)
  saver.stop()
})

test('keep after a loss reads the Draft, keeps the chunk and the words of the person, and saves from the new base', async () => {
  vi.mocked(saveRawDraft).mockResolvedValueOnce(lostAt(5))
  vi.mocked(saveRawDraft).mockResolvedValue(wonAt(7))
  const saver = new DraftSaver(target, clientAnswering('a chunk of a recording', 5))
  saver.edit('my words')
  saver.flush()
  await vi.waitFor(() => expect(saver.getSnapshot().conflict).toBe('wordsMovedToHistory'))

  const merged = await saver.keepWords('my words')

  expect(plainWordsOf(merged ?? '')).toBe('a chunk of a recording\nmy words')
  expect(saver.getSnapshot().conflict).toBeNull()
  await vi.waitFor(() => expect(saveRawDraft).toHaveBeenCalledTimes(2))
  expect(vi.mocked(saveRawDraft).mock.lastCall?.[0].body).toMatchObject({ text: merged, baseRevision: 5 })
  saver.stop()
})

test('keep after a loss changes nothing when the read fails, so the words stay on the page and the notice stays', async () => {
  vi.mocked(saveRawDraft).mockResolvedValueOnce(lostAt(5))
  const queryClient = new QueryClient()
  const read = vi.spyOn(queryClient, 'fetchQuery').mockResolvedValueOnce(entryWithWords('a chunk', 5))
  read.mockRejectedValue(new Error('Network Error'))
  const saver = new DraftSaver(target, queryClient)
  saver.edit('my words')
  saver.flush()
  await vi.waitFor(() => expect(saver.getSnapshot().conflict).toBe('wordsMovedToHistory'))

  await expect(saver.keepWords('my words')).resolves.toBeNull()

  expect(saver.getSnapshot()).toMatchObject({ conflict: 'wordsMovedToHistory', baseRevision: 1 })
  expect(saveRawDraft).toHaveBeenCalledOnce()
  saver.stop()
})

test('keep after a loss stops when the person typed during the read', async () => {
  vi.mocked(saveRawDraft).mockResolvedValueOnce(lostAt(5))
  vi.mocked(saveRawDraft).mockReturnValue(new Promise(() => undefined))
  const queryClient = new QueryClient()
  let answer: (session: SessionDto) => void = () => undefined
  const read = vi.spyOn(queryClient, 'fetchQuery').mockResolvedValueOnce(entryWithWords('a chunk', 5))
  read.mockImplementation(() => new Promise((resolve) => { answer = resolve as typeof answer }))
  const saver = new DraftSaver(target, queryClient)
  saver.edit('my words')
  saver.flush()
  await vi.waitFor(() => expect(saver.getSnapshot().conflict).toBe('wordsMovedToHistory'))

  const keeping = saver.keepWords('my words')
  saver.edit('my words and a key')
  answer(entryWithWords('a chunk', 5))

  await expect(keeping).resolves.toBeNull()
  expect(saver.getSnapshot()).toMatchObject({ conflict: 'wordsMovedToHistory', baseRevision: 1 })
  saver.stop()
})

test('a loss with no page raises a notice that names History', async () => {
  vi.mocked(saveRawDraft).mockResolvedValueOnce(lostAt(5))
  const saver = new DraftSaver(target, clientAnswering('a chunk', 5))
  saver.edit('words of a stale tab')
  saver.flush()

  await vi.waitFor(() => expect(saver.notice()).toMatchObject({ kind: 'inHistory', sessionId: 'session-1' }))

  saver.dismissConflict()
  expect(saver.notice()).toBeNull()
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

test('the stale writer of a key keeps its words beside a key written by this tab', () => {
  vi.mocked(saveRawDraft).mockReturnValue(new Promise(() => undefined))
  const live = writeToOutbox(owner, null, entryWith('words of a live page'))
  const saver = aSaver()

  saver.edit('words of a second page')
  saver.discard()

  expect(live && localStorage.getItem(live)).toContain('words of a live page')
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
  vi.spyOn(queryClient, 'fetchQuery').mockImplementation(() => new Promise((resolve) => { answer = resolve as typeof answer }))
  const saver = new DraftSaver(target, queryClient)

  saver.edit('my words')
  saver.flush()
  await vi.waitFor(() => expect(queryClient.fetchQuery).toHaveBeenCalledOnce())
  saver.edit('my words and more')
  saver.flush()
  await new Promise((resolve) => setTimeout(resolve, 20))
  expect(saveRawDraft).toHaveBeenCalledOnce()

  answer(entryWithWords('newer words', 7))
  await vi.waitFor(() => expect(saveRawDraft).toHaveBeenCalledTimes(2))
  saver.stop()
})

test('a failed read after a loss sends the same save again, and it raises no notice until a read answers', async () => {
  vi.useFakeTimers()
  vi.mocked(saveRawDraft).mockResolvedValue(lostWithNoNumber())
  const queryClient = new QueryClient()
  const read = vi.spyOn(queryClient, 'fetchQuery').mockRejectedValueOnce(new Error('Network Error'))
  read.mockResolvedValue(entryWithWords('newer words', 7))
  const saver = new DraftSaver(target, queryClient)

  saver.edit('my words')
  saver.flush()
  await vi.advanceTimersByTimeAsync(10)
  expect(saver.getSnapshot().conflict).toBeNull()
  expect(saver.getSnapshot().status.kind).toBe('waiting')

  await vi.advanceTimersByTimeAsync(3000)
  expect(saveRawDraft).toHaveBeenCalledTimes(2)
  expect(vi.mocked(saveRawDraft).mock.calls[1]?.[0].body).toEqual(vi.mocked(saveRawDraft).mock.calls[0]?.[0].body)
  expect(saver.getSnapshot().conflict).toBe('wordsMovedToHistory')
  saver.stop()
  vi.useRealTimers()
})

test('words that the Draft holds raise no notice, and the page takes the Draft', async () => {
  vi.mocked(saveRawDraft).mockResolvedValue(lostWithNoNumber())
  const saver = new DraftSaver(target, clientAnswering(doc('my words', 'a chunk'), 7))
  const shown = vi.fn()
  saver.subscribeToNewWords(shown)
  saver.subscribe(() => undefined)

  saver.edit(doc('my words'))
  saver.flush()
  await vi.waitFor(() => expect(saver.getSnapshot().status.kind).toBe('saved'))

  expect(saver.getSnapshot()).toMatchObject({ conflict: null, baseRevision: 7 })
  expect(shown).toHaveBeenCalledWith(doc('my words', 'a chunk'), 'draftHoldsMore')
  saver.stop()
})

test('words that the Draft holds with no page raise no notice of History', async () => {
  vi.mocked(saveRawDraft).mockResolvedValue(lostWithNoNumber())
  const saver = new DraftSaver(target, clientAnswering(doc('my words', 'a chunk'), 7))

  saver.edit(doc('my words'))
  saver.flush()
  await vi.waitFor(() => expect(saver.getSnapshot().status.kind).toBe('saved'))

  expect(saver.notice()).toBeNull()
  saver.stop()
})

test('a person who types during the read keeps the words on the page, and the next save decides', async () => {
  vi.mocked(saveRawDraft).mockResolvedValueOnce(lostWithNoNumber())
  vi.mocked(saveRawDraft).mockReturnValue(new Promise(() => undefined))
  const queryClient = new QueryClient()
  let answer: (session: SessionDto) => void = () => undefined
  vi.spyOn(queryClient, 'fetchQuery').mockImplementation(() => new Promise((resolve) => { answer = resolve as typeof answer }))
  const saver = new DraftSaver(target, queryClient)
  const shown = vi.fn()
  saver.subscribeToNewWords(shown)

  saver.edit(doc('my words'))
  saver.flush()
  await vi.waitFor(() => expect(queryClient.fetchQuery).toHaveBeenCalledOnce())
  saver.edit(doc('my words and a key'))
  answer(entryWithWords(doc('my words', 'a chunk'), 7))
  await vi.waitFor(() => expect(saveRawDraft).toHaveBeenCalledTimes(2))

  expect(shown).not.toHaveBeenCalled()
  expect(saver.getSnapshot().baseRevision).toBe(1)
  saver.stop()
})

test('a Lost state keeps its notice after a reload, and the words open on the page', async () => {
  vi.mocked(saveRawDraft).mockResolvedValue(lostWithNoNumber())
  const before = new DraftSaver(target, clientAnswering('a chunk', 7))
  before.edit('my words')
  before.flush()
  await vi.waitFor(() => expect(before.getSnapshot().conflict).toBe('wordsMovedToHistory'))
  const [kept] = readOutbox(owner)
  expect(kept?.entry).toMatchObject({ change: null, wordsInHistory: 'my words', baseRevision: 1 })
  before.stop()

  const after = new DraftSaver({ ...target, baseRevision: 1 }, new QueryClient(), kept)

  expect(after.getSnapshot().conflict).toBe('wordsMovedToHistory')
  expect(after.wordsToOpen()).toBe('my words')
  expect(after.notice()).toMatchObject({ kind: 'inHistory', words: 'my words' })
  after.dismissConflict()
  expect(readOutbox(owner)).toEqual([])
  after.stop()
})

test('a notice of History offers no discard and no word of a sign-out, because the Server holds the words', async () => {
  vi.mocked(saveRawDraft).mockResolvedValue(lostWithNoNumber())
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
  const stored = storedByAnotherTab(entryWith('words of a closed tab'))

  const saver = new DraftSaver(target, new QueryClient(), stored)

  await vi.waitFor(() => expect(saver.notice()).toMatchObject({ kind: 'joined', sessionId: 'session-1' }))
  saver.stop()
})

test('words of a tab that closed raise no notice when an open page shows them', async () => {
  vi.mocked(saveRawDraft).mockResolvedValue(wonAt(2))
  const stored = storedByAnotherTab(entryWith('words of a closed tab'))

  const saver = new DraftSaver(target, new QueryClient(), stored)
  saver.subscribe(() => undefined)

  await vi.waitFor(() => expect(saver.getSnapshot().status.kind).toBe('saved'))
  expect(saver.notice()).toBeNull()
  saver.stop()
})

test('held words that a newer save pushed out name History, and no notice says that they joined the entry', async () => {
  vi.mocked(saveRawDraft).mockResolvedValue(lostWithNoNumber())
  const stored = storedByAnotherTab(entryWith('', { change: null, held: 'words of a hold' }))

  const saver = new DraftSaver(target, clientAnswering('other words of another hold', 7), stored)

  await vi.waitFor(() => expect(saver.notice()).toMatchObject({ kind: 'inHistory' }))
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

test('a tab that wakes while another tab holds its words in a key raises no notice', async () => {
  vi.useFakeTimers()
  vi.mocked(saveRawDraft).mockRejectedValueOnce(new Error('Network Error'))
  const saver = new DraftSaver(target, clientAnswering('the Draft', 1))
  saver.subscribe(() => undefined)
  saver.edit('words that another tab took')
  saver.flush()
  await vi.advanceTimersByTimeAsync(10)

  removeEachKey()
  storedByAnotherTab(entryWith('words that another tab took'))
  await vi.advanceTimersByTimeAsync(3000)

  expect(saveRawDraft).toHaveBeenCalledOnce()
  expect(saver.getSnapshot().conflict).toBeNull()
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

test('a discard also removes a copy of the same words in a key of another tab', () => {
  vi.mocked(saveRawDraft).mockReturnValue(new Promise(() => undefined))
  const saver = aSaver()
  saver.edit('words of two pages')
  storedByAnotherTab(entryWith('words of two pages'))

  saver.discard()

  expect(readOutbox(owner)).toEqual([])
})

test('the sign-out list names one text once, and its discard takes each copy', () => {
  vi.mocked(saveRawDraft).mockReturnValue(new Promise(() => undefined))
  const saver = draftSaverFor({ ...target, clientSessionId: 'entry-of-two-pages' }, new QueryClient())
  saver.edit('words of two pages')
  storedByAnotherTab(entryWith('words of two pages', { key: 'entry-of-two-pages.raw', clientSessionId: 'entry-of-two-pages' }))

  const listed = unsentWordsOnThisDevice(owner, []).filter(({ words }) => words === 'words of two pages')

  expect(listed).toHaveLength(1)
  listed[0]?.discard()
  expect(wordsInKeys()).not.toContain('words of two pages')
})
