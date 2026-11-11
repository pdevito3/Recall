import { QueryClient } from '@tanstack/react-query'
import { afterEach, expect, test, vi } from 'vitest'
import { render } from 'vitest-browser-react'

import { saveRawDraft } from '@/api/generated/sessions/clients/save-raw-draft'
import { draftSaverFor, stopDraftSavers, type DraftTarget } from '@/domain/sessions/autosave/draft-saver'

import { PageError } from './page-error'

vi.mock('@/api/generated/sessions/clients/save-raw-draft')

// One tab with no Web Locks, as on plain HTTP, so the lock of an entry comes at once. The lock reads the lock manager
// when its module loads, so this runs before the imports.
vi.hoisted(() => {
  Object.defineProperty(navigator, 'locks', { configurable: true, get: () => undefined })
})

afterEach(() => {
  stopDraftSavers()
  vi.restoreAllMocks()
  localStorage.clear()
})

test('shows the words that only the memory holds before it loads the page again', async () => {
  vi.mocked(saveRawDraft).mockReturnValue(new Promise(() => undefined))
  vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
    throw new DOMException('full', 'QuotaExceededError')
  })
  const target: DraftTarget = { owner: 'person-a', channel: 'raw', clientSessionId: 'client-session', startedAt: '2026-11-06T15:30:00.000Z', sessionId: 'session-1', baseRevision: 1, baseText: '' }
  const saver = draftSaverFor(target, new QueryClient())
  saver.subscribe(() => undefined)
  saver.edit(JSON.stringify({ type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'words with no room' }] }] }))
  const screen = await render(<PageError />)

  await screen.getByRole('button', { name: 'Load the page again' }).click()

  await expect.element(screen.getByRole('heading', { level: 1, name: 'This page did not load' })).toBeVisible()
  const list = screen.getByRole('list', { name: 'Entries with words that only this page holds' })
  await expect.element(list.getByText('words with no room')).toBeVisible()
  await expect.element(screen.getByRole('button', { name: 'Leave this page' })).toBeVisible()
})
