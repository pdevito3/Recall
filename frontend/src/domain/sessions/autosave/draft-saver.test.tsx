import { QueryClient } from '@tanstack/react-query'
import { afterEach, expect, test, vi } from 'vitest'

import { addSession } from '@/api/generated/sessions/clients/add-session'
import { saveRawDraft } from '@/api/generated/sessions/clients/save-raw-draft'
import { ProblemDetailsError } from '@/lib/problem-details'

import { DraftSaver, type DraftTarget } from './draft-saver'
import { readOutbox } from './outbox'

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
