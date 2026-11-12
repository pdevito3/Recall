import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { createMemoryHistory, createRootRoute, createRoute, createRouter, Outlet, RouterProvider } from '@tanstack/react-router'
import { afterEach, expect, test, vi } from 'vitest'
import { commands } from 'vitest/browser'
import { render } from 'vitest-browser-react'

import type { SessionDto } from '@/api/generated/models/types/session-dto'
import { AuthKeys } from '@/domain/auth/apis/auth.keys'
import { PersonKeys } from '@/domain/people/apis/person.keys'
import { memberSession } from '@/test/session'

import { discardDraftSavers } from '../autosave/draft-saver'
import { closeEntryLocks } from '../autosave/entry-lock'
import { DraftEditor } from './draft-editor'
import { DraftNotices } from './draft-notices'

const paragraph = (text: string) => JSON.stringify({ type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text }] }] })

const cleanedEntry: SessionDto = {
  id: 'ses_1',
  clientSessionId: '00000000-0000-4000-8000-000000000001',
  startedAt: '2026-11-06T15:30:00+00:00',
  journalingDay: '2026-11-06',
  rawText: paragraph('Walked to the lake.'),
  rawDraftRevision: 1,
  rawLatestRevision: 1,
  rawSavedAt: '2026-11-06T15:30:00+00:00',
  cleanedText: paragraph('I walked to the lake.'),
  cleanedDraftRevision: 2,
  cleanedLatestRevision: 2,
  cleanedSavedAt: '2026-11-06T15:31:00+00:00',
  cleanedHasHandEdits: false,
  synopsis: null,
  cleanupStatus: 'Clean',
  lastCleanupRawRevision: 1,
  cleanupFailure: 'None',
  suggestions: [],
  peopleProposals: [],
  activity: 'None',
  moods: [],
  topics: [],
  personIds: [],
  latitude: null,
  longitude: null,
  version: 2,
  metadataVersion: 1,
}

afterEach(() => {
  discardDraftSavers(cleanedEntry.clientSessionId)
  closeEntryLocks()
  vi.restoreAllMocks()
  localStorage.clear()
})

test('an editor that opens under the shown notices updates no other component during its render', async () => {
  // Arrange
  await commands.answerRequests([{ method: 'GET', path: '/api/v1/me/settings', body: { timeZone: 'UTC', timeZoneIsKnown: true, locationCaptureEnabled: false, requirePeopleProposalApproval: false } }])
  const errors = vi.spyOn(console, 'error')
  const rootRoute = createRootRoute({
    component: () => (
      <>
        <DraftNotices />
        <Outlet />
      </>
    ),
  })
  const startRoute = createRoute({ getParentRoute: () => rootRoute, path: '/', component: () => <h1>Journal</h1> })
  const entryRoute = createRoute({ getParentRoute: () => rootRoute, path: '/sessions/$id', component: () => <DraftEditor session={cleanedEntry} channel="cleaned" /> })
  const router = createRouter({ routeTree: rootRoute.addChildren([startRoute, entryRoute]), history: createMemoryHistory({ initialEntries: ['/'] }) })
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  client.setQueryData(AuthKeys.user(), memberSession)
  client.setQueryData(PersonKeys.directory(), [])
  const screen = await render(
    <QueryClientProvider client={client}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  )
  await expect.element(screen.getByRole('heading', { name: 'Journal' })).toBeVisible()

  // Act
  await router.navigate({ to: '/sessions/$id', params: { id: cleanedEntry.id } })

  // Assert
  await expect.element(screen.getByRole('region', { name: 'Cleaned version' })).toBeVisible()
  const updatesInRender = errors.mock.calls.filter(([message]) => String(message).startsWith('Cannot update a component'))
  expect(updatesInRender).toEqual([])
})
