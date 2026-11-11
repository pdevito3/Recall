import { QueryClient } from '@tanstack/react-query'
import { afterEach, expect, test, vi } from 'vitest'

import { saveRawDraft } from '@/api/generated/sessions/clients/save-raw-draft'
import { draftSaverFor, stopDraftSavers, type DraftTarget } from '@/domain/sessions/autosave/draft-saver'

import { leaveTheApp, leftTheApp } from './leave-the-app'

vi.mock('@/api/generated/sessions/clients/save-raw-draft')

// One tab with no Web Locks, as on plain HTTP, so the lock of an entry comes at once. The lock reads the lock manager
// when its module loads, so this runs before the imports.
vi.hoisted(() => {
  Object.defineProperty(navigator, 'locks', { configurable: true, get: () => undefined })
})

/** A replace with only a hash stays on the page of the test, so a test that fails does not take the page away. */
const nowhere = '#left-the-app'

afterEach(() => {
  stopDraftSavers()
  vi.restoreAllMocks()
  localStorage.clear()
  history.replaceState(null, '', window.location.pathname + window.location.search)
})

test('lists the words that only the memory holds, because a stopped saver holds none', () => {
  vi.mocked(saveRawDraft).mockReturnValue(new Promise(() => undefined))
  vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
    throw new DOMException('full', 'QuotaExceededError')
  })
  const target: DraftTarget = { owner: 'person-a', channel: 'raw', clientSessionId: 'client-session', startedAt: '2026-11-06T15:30:00.000Z', sessionId: 'session-1', baseRevision: 1, baseText: '' }
  const saver = draftSaverFor(target, new QueryClient())
  saver.subscribe(() => undefined)
  saver.edit('words with no room')

  leaveTheApp({ why: 'sessionEnded', to: nowhere })

  expect(leftTheApp()).toMatchObject({ why: 'sessionEnded', words: ['words with no room'] })
  expect(window.location.hash).toBe('')
})
