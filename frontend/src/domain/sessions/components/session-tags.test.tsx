import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { expect, onTestFinished, test } from 'vitest'
import { commands, page } from 'vitest/browser'
import { render } from 'vitest-browser-react'

import type { PersonDto } from '@/api/generated/models/types/person-dto'
import type { SessionDto } from '@/api/generated/models/types/session-dto'
import '@/lib/api-client'
import { signedInMember } from '@/test/session'

import { SessionTags } from './session-tags'

const longLabel = `Maximilian${'b'.repeat(70)}`
const person: PersonDto = { id: 'per_1', label: longLabel, fullName: null, notes: null, version: 1 }

const session: SessionDto = {
  id: 'ses_1',
  clientSessionId: 'c1',
  startedAt: '2026-11-07T10:00:00Z',
  journalingDay: '2026-11-07',
  rawText: '',
  rawDraftRevision: 1,
  rawLatestRevision: 1,
  cleanedText: '',
  cleanedDraftRevision: 0,
  cleanedLatestRevision: 0,
  cleanedHasHandEdits: false,
  cleanupStatus: 'Clean',
  lastCleanupRawRevision: 1,
  cleanupBaseCleanedRevision: 0,
  waitingCleanupRevision: null,
  cleanupFailure: 'None',
  suggestions: [],
  peopleProposals: [],
  activity: 'None',
  moods: [],
  topics: [],
  personIds: [person.id],
  version: 1,
  metadataVersion: 0,
}

test('a long name of a tagged person shows whole, on more lines, in a narrow window', async () => {
  await page.viewport(320, 800)
  onTestFinished(() => page.viewport(1280, 800))
  await commands.answerRequests([
    ...signedInMember,
    { method: 'GET', path: '/api/v1/people', headers: { 'x-pagination': JSON.stringify({ totalCount: 1, pageSize: 100, currentPageSize: 1, currentStartIndex: 1, currentEndIndex: 1, pageNumber: 1, totalPages: 1, hasPrevious: false, hasNext: false }) }, body: [person] },
  ])
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const screen = await render(
    <QueryClientProvider client={client}>
      <SessionTags session={session} />
    </QueryClientProvider>,
  )

  const name = screen.getByText(`@${longLabel}`)
  await expect.element(name).toBeVisible()
  const element = name.element() as HTMLElement
  expect(element.scrollWidth).toBeLessThanOrEqual(element.clientWidth)
  expect(element.getBoundingClientRect().right).toBeLessThanOrEqual(320)
})
