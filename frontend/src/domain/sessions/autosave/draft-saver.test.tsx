import { QueryClient } from '@tanstack/react-query'
import { afterEach, expect, test, vi } from 'vitest'

import { addSession } from '@/api/generated/sessions/clients/add-session'
import type { SessionDto } from '@/api/generated/models/types/session-dto'
import { saveRawDraft } from '@/api/generated/sessions/clients/save-raw-draft'
import { ProblemDetailsError } from '@/lib/problem-details'

import { DraftSaver, type DraftTarget } from './draft-saver'
import { readHoldFromOutbox, readOutbox, readOutboxEntry } from './outbox'

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

  expect(readHoldFromOutbox(owner, key)?.change.value).toBe('held words')
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

  expect(readHoldFromOutbox(owner, key)).toBeNull()
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
  const secondTab = new DraftSaver(target, queryClient, null, held('held words'))
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
  const secondTab = new DraftSaver(target, clientAnswering('words on the server'), null, held('held words'))

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

  expect(readHoldFromOutbox(owner, key)?.change.value).toBe('held words')
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
