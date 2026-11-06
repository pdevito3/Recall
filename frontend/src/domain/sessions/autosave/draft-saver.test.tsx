import { QueryClient } from '@tanstack/react-query'
import { afterEach, expect, test, vi } from 'vitest'

import { addSession } from '@/api/generated/sessions/clients/add-session'
import type { SessionDto } from '@/api/generated/models/types/session-dto'
import { saveRawDraft } from '@/api/generated/sessions/clients/save-raw-draft'
import { ProblemDetailsError } from '@/lib/problem-details'

import { plainWordsOf } from '../editor/content'
import { DraftSaver, draftNotices, draftSaverFor, type DraftTarget } from './draft-saver'
import { readHoldsFromOutbox, readOutbox, readOutboxEntry, writeHoldToOutbox, writeToOutbox } from './outbox'

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
  latestRevision: 1,
}

const aSaver = () => new DraftSaver(target, new QueryClient())

afterEach(() => {
  localStorage.clear()
  vi.resetAllMocks()
})

test('a discarded saver writes no words back to storage', () => {
  const saver = aSaver()

  saver.discard()
  saver.edit('words of a discard')

  expect(readOutbox(owner)).toEqual([])
})

test('a stopped saver keeps its words in storage', () => {
  const saver = aSaver()

  saver.stop()
  saver.edit('words at a logout')

  expect(readOutbox(owner).map((entry) => entry.change.value)).toEqual(['words at a logout'])
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

const key = 'client-session.raw'
const held = (words: string) => ({ ...target, key, channel: 'raw' as const, change: { value: words, savedAt: '2026-11-06T15:31:00.000Z' }, isHold: true })
const wonAt = (revision: number) => ({ outcome: 'Won', draftRevision: revision, latestRevision: revision, savedAt: '2026-11-06T15:31:00+00:00', cleanupStatus: 'Clean', version: revision }) as never
const entryWithWords = (rawText: string) => ({ id: 'session-1', rawText, rawDraftRevision: 1, rawLatestRevision: 1 }) as SessionDto
const clientAnswering = (rawText: string) => {
  const queryClient = new QueryClient()
  vi.spyOn(queryClient, 'fetchQuery').mockResolvedValue(entryWithWords(rawText))
  return queryClient
}
const lastSavedText = () => vi.mocked(saveRawDraft).mock.lastCall?.[0].body.text
const holdIsTaken = (saver: DraftSaver) => vi.waitFor(() => expect(saver.joinHoldInto('probe')).not.toBe('probe'))

test('a key on another page during a hold does not remove the held words from storage', () => {
  vi.mocked(saveRawDraft).mockResolvedValue(wonAt(2))
  const saver = aSaver()

  saver.holdInOutbox('held words')
  saver.edit('a key on the entry page')

  expect(readHoldsFromOutbox(owner, key)[0]?.entry.change.value).toBe('held words')
  expect(readOutboxEntry(owner, key)?.change.value).toBe('a key on the entry page')
  expect(saver.wordsInHold()).toBe('held words')
  saver.stop()
})

test('the join puts the held words after the newest words of the person, and removes the hold after the words are in the outbox', async () => {
  vi.mocked(saveRawDraft).mockReturnValue(new Promise(() => undefined))
  const saver = new DraftSaver(target, clientAnswering('words on the server'))
  const shown = vi.fn()
  saver.subscribeToHoldJoin(shown)
  saver.holdInOutbox('held words')
  saver.edit('a key on the entry page')

  await vi.waitFor(() => expect(saver.wordsInHold()).toBe(null))

  expect(readHoldsFromOutbox(owner, key)).toEqual([])
  expect(readOutboxEntry(owner, key)?.change.value).toContain('a key on the entry page')
  expect(readOutboxEntry(owner, key)?.change.value).toContain('held words')
  expect(shown).toHaveBeenCalledWith(readOutboxEntry(owner, key)?.change.value)
  saver.stop()
})

test('an open page gets the joined words, so that its next key cannot save the old text over them', async () => {
  vi.mocked(saveRawDraft).mockResolvedValue(wonAt(2))
  const saver = new DraftSaver(target, clientAnswering('words on the server'))
  const shown = vi.fn()
  saver.subscribeToHoldJoin(shown)

  saver.holdInOutbox('held words')

  await vi.waitFor(() => expect(shown).toHaveBeenCalledOnce())
  expect(shown.mock.lastCall?.[0]).toContain('words on the server')
  expect(shown.mock.lastCall?.[0]).toContain('held words')
  saver.stop()
})

test('only one tab joins a hold: a second tab waits, and the words go out once', async () => {
  vi.mocked(saveRawDraft).mockResolvedValue(wonAt(2))
  const firstTab = aSaver()
  firstTab.takeHold()
  firstTab.holdInOutbox('held words')
  await holdIsTaken(firstTab).catch(() => undefined)
  firstTab.holdInOutbox('held words')

  const queryClient = clientAnswering('words on the server')
  const secondTab = new DraftSaver(target, queryClient, null, [held('held words')])
  await new Promise((resolve) => setTimeout(resolve, 50))
  expect(queryClient.fetchQuery).not.toHaveBeenCalled()
  expect(secondTab.joinHoldInto('words on the server')).toBe('words on the server')

  firstTab.flush()
  firstTab.joinHoldInto('words on the server')
  firstTab.flush()
  await vi.waitFor(() => expect(secondTab.wordsInHold()).toBe(null))

  expect(queryClient.fetchQuery).not.toHaveBeenCalled()
  firstTab.stop()
  secondTab.stop()
})

test('a tab that closes hands its hold to the next tab, and the words are not lost', async () => {
  vi.mocked(saveRawDraft).mockResolvedValue(wonAt(2))
  const firstTab = aSaver()
  firstTab.takeHold()
  firstTab.holdInOutbox('held words')
  await new Promise((resolve) => setTimeout(resolve, 20))
  const secondTab = new DraftSaver(target, clientAnswering('words on the server'), null, [held('held words')])

  firstTab.stop()

  await vi.waitFor(() => expect(lastSavedText()).toContain('held words'))
  expect(lastSavedText()).toContain('words on the server')
  secondTab.stop()
})

test('a hold keeps its own key, so a regular save in another tab does not overwrite it', () => {
  const first = aSaver()
  first.holdInOutbox('held words')

  const second = aSaver()
  second.edit('words of the other tab')

  expect(readHoldsFromOutbox(owner, key)[0]?.entry.change.value).toBe('held words')
  first.stop()
  second.stop()
})

test('a full storage tells the person at once that the words are only in this page, and a later write ends the notice', () => {
  const setItem = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
    throw new DOMException('full', 'QuotaExceededError')
  })
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

test('a stopped saver no longer warns of a loss, because the person chose', () => {
  vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
    throw new DOMException('full', 'QuotaExceededError')
  })
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

const lockIsGiven = () => new Promise((resolve) => setTimeout(resolve, 20))

const fullStorage = () =>
  vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
    throw new DOMException('full', 'QuotaExceededError')
  })

test('two tabs that type different words during a hold both reach the entry, in each order of the keys', async () => {
  for (const [first, second] of [['aaa', 'bbb'], ['bbb', 'aaa']] as const) {
    localStorage.clear()
    vi.mocked(saveRawDraft).mockResolvedValue(wonAt(2))
    const firstTab = aSaver()
    const secondTab = aSaver()
    firstTab.takeHold()
    secondTab.takeHold()
    firstTab.holdInOutbox(first)
    await lockIsGiven()
    secondTab.holdInOutbox(second)
    firstTab.holdInOutbox(`${first} more`)

    const joined = firstTab.joinHoldInto('')
    await vi.waitFor(() => expect(secondTab.wordsInHold()).toBeNull())

    expect(joined).toContain(`${first} more`)
    expect(joined).toContain(second)
    firstTab.stop()
    secondTab.stop()
  }
})

test('a key in a waiting tab reaches the entry, also when the first tab joins the older words of the hold', async () => {
  vi.mocked(saveRawDraft).mockResolvedValue(wonAt(2))
  writeHoldToOutbox(owner, held('held words'), 'a closed tab')
  const firstTab = new DraftSaver(target, new QueryClient(), null, [held('held words')])
  firstTab.takeHold()
  await lockIsGiven()
  const waitingTab = aSaver()
  waitingTab.takeHold()
  waitingTab.holdInOutbox('held words x')

  const joined = firstTab.joinHoldInto('')

  expect(plainWordsOf(joined)).toContain('held words x')
  expect(plainWordsOf(joined).match(/held words/g)).toHaveLength(1)
  await vi.waitFor(() => expect(waitingTab.wordsInHold()).toBeNull())
  expect(readHoldsFromOutbox(owner, key)).toEqual([])
  firstTab.stop()
  waitingTab.stop()
})

test('a tab with a failed write keeps its newer words when the lock comes', async () => {
  const firstTab = aSaver()
  firstTab.takeHold()
  firstTab.holdInOutbox('first tab')
  await lockIsGiven()
  const secondTab = aSaver()
  secondTab.takeHold()
  secondTab.holdInOutbox('second')
  const setItem = fullStorage()
  secondTab.holdInOutbox('second tab typed more')
  setItem.mockRestore()

  firstTab.joinHoldInto('')
  await vi.waitFor(() => expect(secondTab.joinHoldInto('')).toContain('second tab typed more'))

  firstTab.stop()
  secondTab.stop()
})

test('a join on a full storage keeps the only saved copy until the queue is in the outbox', async () => {
  vi.mocked(saveRawDraft).mockReturnValue(new Promise(() => undefined))
  const saver = aSaver()
  saver.takeHold()
  saver.holdInOutbox('held words')
  await lockIsGiven()
  const setItem = fullStorage()

  saver.joinHoldInto('words on the server')

  expect(saver.wordsInHold()).toBeNull()
  expect(readHoldsFromOutbox(owner, key)[0]?.entry.change.value).toBe('held words')
  expect(saver.notice()).toMatchObject({ kind: 'onlyInMemory' })

  setItem.mockRestore()
  saver.edit('words with room')

  expect(readHoldsFromOutbox(owner, key)).toEqual([])
  saver.stop()
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

test('a hold that the browser can write does not end the warning for a queue that it could not write', () => {
  vi.mocked(saveRawDraft).mockReturnValue(new Promise(() => undefined))
  const saver = aSaver()
  saver.takeHold()
  const setItem = fullStorage()
  saver.edit('words with no room')
  setItem.mockRestore()

  saver.holdInOutbox('a short hold')

  expect(saver.isHoldingWordsOnlyInMemory()).toBe(true)
  saver.stop()
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

test('a discard in one tab leaves the words that another tab typed in its hold', async () => {
  const firstTab = aSaver()
  const secondTab = aSaver()
  secondTab.takeHold()
  secondTab.holdInOutbox('words of the second tab')
  await lockIsGiven()

  firstTab.discard()

  expect(readHoldsFromOutbox(owner, key)[0]?.entry.change.value).toBe('words of the second tab')
  expect(plainWordsOf(secondTab.joinHoldInto(''))).toContain('words of the second tab')
  secondTab.stop()
})

test('a discard leaves a slot whose words only appear inside the words that this tab showed', async () => {
  writeHoldToOutbox(owner, held('went to the store with Sam'), 'closed tab')
  const saver = new DraftSaver(target, new QueryClient(), null, [held('went to the store with Sam')])
  saver.takeHold()
  await lockIsGiven()
  writeHoldToOutbox(owner, held('store'), 'another tab')

  saver.discard()

  expect(readHoldsFromOutbox(owner, key).map(({ entry }) => entry.change.value)).toEqual(['store'])
})

test('a discard during a join that waits for room removes the words of that join', async () => {
  vi.mocked(saveRawDraft).mockReturnValue(new Promise(() => undefined))
  writeHoldToOutbox(owner, held('held words'), 'closed tab')
  const saver = new DraftSaver(target, new QueryClient(), null, [held('held words')])
  saver.takeHold()
  await lockIsGiven()
  const setItem = fullStorage()
  saver.joinHoldInto('')
  setItem.mockRestore()

  saver.discard()

  expect(readHoldsFromOutbox(owner, key)).toEqual([])
})

test('a discard removes the words that this tab showed, also from a closed tab', async () => {
  writeHoldToOutbox(owner, held('words of a closed tab'), 'closed tab')
  const saver = new DraftSaver(target, new QueryClient(), null, [held('words of a closed tab')])
  saver.takeHold()
  await lockIsGiven()

  saver.discard()

  expect(readHoldsFromOutbox(owner, key)).toEqual([])
})

test('a late removal of joined holds leaves the words that a tab typed after the join', async () => {
  vi.mocked(saveRawDraft).mockReturnValue(new Promise(() => undefined))
  const firstTab = aSaver()
  const secondTab = aSaver()
  firstTab.takeHold()
  secondTab.takeHold()
  firstTab.holdInOutbox('first')
  await lockIsGiven()
  secondTab.holdInOutbox('second')
  const setItem = fullStorage()
  firstTab.joinHoldInto('')
  setItem.mockRestore()

  secondTab.holdInOutbox('second and more')
  firstTab.edit('a write that works')

  await vi.waitFor(() => expect(plainWordsOf(readHoldsFromOutbox(owner, key)[0]?.entry.change.value ?? '')).toBe('second and more'))
  await lockIsGiven()
  expect(plainWordsOf(secondTab.joinHoldInto(''))).toContain('second and more')
  firstTab.stop()
  secondTab.stop()
})

test('a move to a new entry on a full storage keeps the saved holds of the old entry', async () => {
  writeHoldToOutbox(owner, held('held words'), 'closed tab')
  const saver = new DraftSaver(target, new QueryClient(), null, [held('held words')])
  saver.takeHold()
  await lockIsGiven()
  fullStorage()
  vi.mocked(saveRawDraft).mockRejectedValueOnce(new ProblemDetailsError({ type: 'about:blank', title: 'Gone', status: 404 }))
  vi.mocked(addSession).mockReturnValue(new Promise(() => undefined))

  saver.edit('words on a deleted entry')
  saver.flush()
  await vi.waitFor(() => expect(saver.getSnapshot().movedFromDeletedEntry).toBe(true))

  expect(readHoldsFromOutbox(owner, key)[0]?.entry.change.value).toBe('held words')
  saver.stop()
})

test('a move to a new entry on a full storage keeps the saved queue of the old entry', async () => {
  const stored = { ...target, key, channel: 'raw' as const, change: { value: 'saved words', savedAt: '2026-11-06T15:31:00.000Z' } }
  writeToOutbox(owner, stored)
  fullStorage()
  vi.mocked(saveRawDraft).mockRejectedValueOnce(new ProblemDetailsError({ type: 'about:blank', title: 'Gone', status: 404 }))
  vi.mocked(addSession).mockReturnValue(new Promise(() => undefined))

  const saver = new DraftSaver(target, new QueryClient(), stored)
  saver.flush()
  await vi.waitFor(() => expect(saver.getSnapshot().movedFromDeletedEntry).toBe(true))

  expect(readOutboxEntry(owner, key)?.change.value).toBe('saved words')
  saver.stop()
})
