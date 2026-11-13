import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { createMemoryHistory, createRouter, RouterProvider } from '@tanstack/react-router'
import { expect, test } from 'vitest'
import { commands, page } from 'vitest/browser'
import { render } from 'vitest-browser-react'

import type { RecordingDto } from '@/api/generated/models/types/recording-dto'
import type { SessionDto } from '@/api/generated/models/types/session-dto'
import type { UserSettingDto } from '@/api/generated/models/types/user-setting-dto'
import { routeTree } from '@/routeTree.gen'
import type { Answer } from '@/test/answer'
import { signedInMember } from '@/test/session'

const doc = (words: string) => JSON.stringify({ type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: words }] }] })

const entry: SessionDto = {
  id: 'ses_1',
  clientSessionId: 'c1',
  startedAt: '2026-11-07T18:30:00Z',
  journalingDay: '2026-11-07',
  rawText: doc('Walked to the lake after work and met Sam for coffee.'),
  rawDraftRevision: 1,
  rawLatestRevision: 1,
  cleanedText: '',
  cleanedDraftRevision: 0,
  cleanedLatestRevision: 0,
  cleanedHasHandEdits: false,
  cleanupStatus: 'NotRun',
  lastCleanupRawRevision: 0,
  cleanupBaseCleanedRevision: 0,
  waitingCleanupRevision: null,
  cleanupFailure: 'None',
  suggestions: [],
  peopleProposals: [],
  activity: 'None',
  moods: [],
  topics: [],
  personIds: [],
  version: 1,
  metadataVersion: 0,
}

const noMorePeople = {
  'x-pagination': JSON.stringify({ totalCount: 0, pageSize: 100, currentPageSize: 0, currentStartIndex: 1, currentEndIndex: 0, pageNumber: 1, totalPages: 1, hasPrevious: false, hasNext: false }),
}

const setting: UserSettingDto = { timeZoneMode: 'Fixed', timeZone: 'Europe/Lisbon', timeZoneIsKnown: true, locationCaptureEnabled: false, requirePeopleProposalApproval: false, version: 1 }

const recording = (change: Partial<RecordingDto>): RecordingDto => ({
  id: 'rec_1',
  sessionId: 'ses_1',
  clientRecordingId: 'cr1',
  format: 'Webm',
  engine: 'Cloud',
  status: 'Transcribed',
  problem: 'None',
  problemText: null,
  storedSlices: [0],
  byteLength: 48_000n,
  transcribedMs: 3_000n,
  version: 1,
  ...change,
})

// The contract types an int64 as a bigint, and the wire has a JSON number.
const asJson = (body: unknown) => JSON.stringify(body, (_, value: unknown) => (typeof value === 'bigint' ? Number(value) : value))

const refused =
  'The audio is stored, but the provider that makes the text refused it. Ask an Admin to check the key, the model, and the limits of the provider, then try again.'

async function openEntry(answers: Answer[]) {
  await page.viewport(1280, 800)
  await commands.answerRequests([
    ...signedInMember,
    { method: 'GET', path: '/api/v1/sessions/ses_1', body: entry },
    { method: 'GET', path: '/api/v1/me/settings', body: setting },
    { method: 'GET', path: '/api/v1/people', headers: noMorePeople, body: [] },
    { method: 'GET', path: '/api/v1/model-providers/active', body: { name: 'Claude' } },
    ...answers,
  ])
  const router = createRouter({ routeTree, history: createMemoryHistory({ initialEntries: ['/sessions/ses_1'] }) })
  return render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  )
}

test('the entry page lists the Recordings of the entry, and a failed one says what went wrong and what to do', async () => {
  // Arrange
  const screen = await openEntry([
    {
      method: 'GET',
      path: '/api/v1/sessions/ses_1/recordings',
      contentType: 'application/json',
      body: asJson([recording({}), recording({ id: 'rec_2', clientRecordingId: 'cr2', status: 'Failed', problem: 'ProviderRefused', problemText: refused })]),
    },
  ])

  // Assert
  const card = screen.getByRole('region', { name: 'Recordings' })
  await expect.element(card).toBeVisible()
  await expect.element(card.getByRole('listitem').filter({ hasText: 'Recording 1' }).getByText('The text of this audio is done.')).toBeVisible()
  const failed = card.getByRole('listitem').filter({ hasText: 'Recording 2' })
  await expect.element(failed.getByText('No text came from this audio.')).toBeVisible()
  await expect.element(failed.getByText(refused)).toBeVisible()
  await expect.element(card.getByText(/ProviderRefused/)).not.toBeInTheDocument()
})
