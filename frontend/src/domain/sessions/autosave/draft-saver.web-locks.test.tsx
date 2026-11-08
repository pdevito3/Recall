import { QueryClient } from '@tanstack/react-query'
import { afterEach, expect, test, vi } from 'vitest'

import { saveRawDraft } from '@/api/generated/sessions/clients/save-raw-draft'

import { draftSaverFor, resumeUnsentDrafts, type DraftTarget } from './draft-saver'
import { closeEntryLocks } from './entry-lock'
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
