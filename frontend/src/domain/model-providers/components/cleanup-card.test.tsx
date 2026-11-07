import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { expect, test } from 'vitest'
import { commands, userEvent } from 'vitest/browser'
import { render } from 'vitest-browser-react'

import type { SessionDto } from '@/api/generated/models/types/session-dto'

import { CleanupCard } from './cleanup-card'

const doc = (words: string) => JSON.stringify({ type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: words }] }] })

const session = (change: Partial<SessionDto>): SessionDto => ({
  id: 'ses_1',
  clientSessionId: 'c1',
  startedAt: '2026-11-07T10:00:00Z',
  journalingDay: '2026-11-07',
  rawText: doc('raw words'),
  rawDraftRevision: 1,
  rawLatestRevision: 1,
  cleanedText: doc('first clean words'),
  cleanedDraftRevision: 1,
  cleanedLatestRevision: 1,
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
  personIds: [],
  version: 1,
  metadataVersion: 0,
  ...change,
})

async function renderCard(answers: Parameters<typeof commands.answerRequests>[0]) {
  await commands.answerRequests([{ method: 'GET', path: '/api/v1/model-providers/active', body: { name: 'Mine' } }, ...answers])
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <CleanupCard sessionId="ses_1" />
    </QueryClientProvider>,
  )
}

test('a hand edit during a run says that Cleaned keeps the words and does not say that Cleaned is current', async () => {
  const screen = await renderCard([
    {
      method: 'GET',
      path: '/api/v1/sessions/ses_1',
      body: session({ cleanupStatus: 'Running', cleanedHasHandEdits: true, cleanedDraftRevision: 2, cleanedLatestRevision: 2, cleanupBaseCleanedRevision: 1, cleanedText: doc('my own words') }),
    },
  ])

  await expect.element(screen.getByText('Cleaned changed during this Cleanup. Cleaned keeps your words, and the result will wait in History.')).toBeVisible()
  await expect.element(screen.getByText('Cleaned is current.')).not.toBeInTheDocument()
})

test('a hand edit before a run says that this Cleanup replaces Cleaned', async () => {
  const screen = await renderCard([
    { method: 'GET', path: '/api/v1/sessions/ses_1', body: session({ cleanupStatus: 'Running', cleanedHasHandEdits: true, cleanedDraftRevision: 2, cleanedLatestRevision: 2, cleanupBaseCleanedRevision: 2 }) },
  ])

  await expect.element(screen.getByText('You edited Cleaned by hand. This Cleanup replaces Cleaned, and your edit stays as a Revision.')).toBeVisible()
})

test('a result that waits in History is not called current, and the person can use it', async () => {
  const waiting = session({ cleanedHasHandEdits: true, cleanedDraftRevision: 2, cleanedLatestRevision: 3, cleanupBaseCleanedRevision: 1, waitingCleanupRevision: 3, cleanedText: doc('my own words') })
  const screen = await renderCard([
    { method: 'GET', path: '/api/v1/sessions/ses_1', body: waiting },
    { method: 'POST', path: '/api/v1/sessions/ses_1/cleanup/waiting/apply', body: session({ cleanedDraftRevision: 4, cleanedLatestRevision: 4, cleanedText: doc('second clean words') }) },
  ])

  await expect.element(screen.getByText('The last Cleanup ended, and Cleaned kept your words.')).toBeVisible()
  await expect.element(screen.getByText(/the result waits in History as Revision 3/)).toBeVisible()
  await expect.element(screen.getByText('Cleaned is current.')).not.toBeInTheDocument()
  await expect.element(screen.getByText(/You edited Cleaned by hand/)).not.toBeInTheDocument()

  await userEvent.click(screen.getByRole('button', { name: 'Use the Cleanup result' }))

  await expect.element(screen.getByText('second clean words')).toBeVisible()
  await expect.element(screen.getByText('Cleaned is current.')).toBeVisible()
})
