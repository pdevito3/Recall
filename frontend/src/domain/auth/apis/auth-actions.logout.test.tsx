import { QueryClient } from '@tanstack/react-query'
import { afterEach, expect, test, vi } from 'vitest'

import { saveRawDraft } from '@/api/generated/sessions/clients/save-raw-draft'
import { draftSaverFor, stopDraftSavers, type DraftTarget } from '@/domain/sessions/autosave/draft-saver'
import { apiClient } from '@/lib/api-client'

import { logout } from './auth-actions'
import { leftTheApp } from './leave-the-app'

vi.mock('@/api/generated/sessions/clients/save-raw-draft')

// One tab with no Web Locks, as on plain HTTP, so the lock of an entry comes at once. The lock reads the lock manager
// when its module loads, so this runs before the imports.
vi.hoisted(() => {
  Object.defineProperty(navigator, 'locks', { configurable: true, get: () => undefined })
})

/** A replace with only a hash stays on the page of the test, so a test that fails does not take the page away. */
const nowhere = '#signed-out'

afterEach(() => {
  stopDraftSavers()
  vi.restoreAllMocks()
  localStorage.clear()
  window.location.hash = ''
})

test('lists the words that the person typed while the Server signed out, because the answer of the sign-out does not stop the savers', async () => {
  let answer!: (answer: unknown) => void
  const post = vi.spyOn(apiClient, 'post').mockReturnValue(new Promise((settle) => (answer = settle)) as never)
  const signingOut = logout()
  await vi.waitFor(() => expect(post).toHaveBeenCalledOnce(), { timeout: 10_000 })

  vi.mocked(saveRawDraft).mockReturnValue(new Promise(() => undefined))
  vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
    throw new DOMException('full', 'QuotaExceededError')
  })
  const target: DraftTarget = { owner: 'person-a', channel: 'raw', clientSessionId: 'client-session', startedAt: '2026-11-06T15:30:00.000Z', sessionId: 'session-1', baseRevision: 1, baseText: '' }
  const saver = draftSaverFor(target, new QueryClient())
  saver.subscribe(() => undefined)
  saver.edit('typed while the Server signed out')
  answer({ status: 200, data: { provider_logout_url: nowhere } })
  await signingOut

  expect(leftTheApp()).toMatchObject({ why: 'signedOut', words: ['typed while the Server signed out'] })
  expect(window.location.hash).toBe('')
}, 15_000)
