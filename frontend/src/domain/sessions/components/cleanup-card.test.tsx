import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { createMemoryHistory, createRootRoute, createRouter, RouterProvider } from '@tanstack/react-router'
import { expect, onTestFinished, test } from 'vitest'
import { commands, page, userEvent } from 'vitest/browser'
import { render } from 'vitest-browser-react'

import type { SessionDto } from '@/api/generated/models/types/session-dto'
// The app sends each request through this client, and it gives the words of a failure.
import '@/lib/api-client'
import { member, signedInMember } from '@/test/session'

import { useSession } from '../apis/get-session'
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

const revision = (number: number, text: string) => ({ number, savedAt: '2026-11-07T10:00:00Z', text })

async function renderCard(answers: Parameters<typeof commands.answerRequests>[0]) {
  await commands.answerRequests([{ method: 'GET', path: '/api/v1/model-providers/active', body: { name: 'Mine' } }, ...answers])
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  // The card can show a link, and a link needs a router.
  const router = createRouter({ routeTree: createRootRoute({ component: CleanupOfTheEntry }), history: createMemoryHistory() })
  return render(
    <QueryClientProvider client={client}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  )
}

// The entry page reads the Session, and the card reads it from the page.
function CleanupOfTheEntry() {
  const session = useSession('ses_1')
  return session.data ? <CleanupCard session={session.data} /> : null
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
    { method: 'GET', path: '/api/v1/sessions/ses_1/cleaned-revisions/3', body: revision(3, doc('second clean words')) },
    { method: 'POST', path: '/api/v1/sessions/ses_1/cleanup/waiting/apply', body: session({ cleanedDraftRevision: 4, cleanedLatestRevision: 4, cleanedText: doc('second clean words') }) },
  ])

  await expect.element(screen.getByText('The last Cleanup ended, and Cleaned kept your words.')).toBeVisible()
  await expect.element(screen.getByText(/A Cleanup result waits in History as Revision 3/)).toBeVisible()
  await expect.element(screen.getByText('Cleaned is current.')).not.toBeInTheDocument()
  await expect.element(screen.getByText(/You edited Cleaned by hand/)).not.toBeInTheDocument()

  await userEvent.click(screen.getByRole('button', { name: 'Use the Cleanup result' }))

  await expect.element(screen.getByText('Cleaned is current.')).toBeVisible()
  await expect.element(screen.getByText(/A Cleanup result waits in History/)).not.toBeInTheDocument()
})

const waitingSince = (change: Partial<SessionDto>) =>
  session({ cleanedHasHandEdits: true, cleanedDraftRevision: 2, cleanedLatestRevision: 3, cleanupBaseCleanedRevision: 2, waitingCleanupRevision: 3, cleanedText: doc('my own words'), ...change })

for (const { name, change, status } of [
  { name: 'your words changed', change: { cleanupStatus: 'Stale', rawDraftRevision: 2 }, status: 'Your words changed after the last Cleanup.' },
  { name: 'a later run failed', change: { cleanupStatus: 'Failed', cleanupFailure: 'ProviderRefused' }, status: 'The last Cleanup did not finish. Your words did not change.' },
  { name: 'a later run was stopped', change: { cleanupStatus: 'Failed', cleanupFailure: 'Stopped' }, status: 'You stopped the last Cleanup. Your words did not change, and you can run it again.' },
] satisfies { name: string; change: Partial<SessionDto>; status: string }[]) {
  test(`when ${name}, the card says so first, and the waiting result is a fact of its own`, async () => {
    // Arrange
    const screen = await renderCard([
      { method: 'GET', path: '/api/v1/sessions/ses_1', body: waitingSince(change) },
      { method: 'GET', path: '/api/v1/sessions/ses_1/cleaned-revisions/3', body: revision(3, doc('second clean words')) },
    ])

    // Assert
    await expect.element(screen.getByRole('status')).toHaveTextContent(status)
    await expect.element(screen.getByText(/A Cleanup result waits in History as Revision 3, because Cleaned changed during its run/)).toBeVisible()
    await expect.element(screen.getByText(/Cleaned changed during the last Cleanup/)).not.toBeInTheDocument()
    await expect.element(screen.getByText('The last Cleanup ended, and Cleaned kept your words.')).not.toBeInTheDocument()
  })
}

const failedOnAnUnusableAnswer = { method: 'GET', path: '/api/v1/sessions/ses_1', body: session({ cleanupStatus: 'Failed', cleanupFailure: 'BadModelOutput' }) } as const
const unusableAnswer = 'The model provider sent an answer that the app cannot use. Most often the model is too small for this work. Try again.'
const forAMember = `${unusableAnswer} If it happens often, an Admin can pick a stronger model or test the connection.`

test('an answer that the app cannot use names the model as the likely cause, and the person can run Cleanup again', async () => {
  // Arrange
  const screen = await renderCard([...signedInMember, failedOnAnUnusableAnswer])

  // Assert
  await expect.element(screen.getByRole('status')).toHaveTextContent('The last Cleanup did not finish. Your words did not change.')
  await expect.element(screen.getByRole('alert')).toHaveTextContent(forAMember)
  await expect.element(screen.getByRole('alert').getByRole('link')).not.toBeInTheDocument()
  await expect.element(screen.getByRole('button', { name: 'Run Cleanup again' })).toBeEnabled()
})

test('an Admin who sees an answer that the app cannot use gets a link to the Model providers page', async () => {
  // Arrange
  const admin = { ...member, role: 'Admin', permissions: ['manage_model_providers'] }
  const screen = await renderCard([...signedInMember, { method: 'GET', path: `/api/v1/users/by-identifier/${member.identifier}`, body: admin }, failedOnAnUnusableAnswer])

  // Assert
  const alert = screen.getByRole('alert')
  await expect.element(alert).toHaveTextContent(`${unusableAnswer} If it happens often, pick a stronger model or test the connection on the Model providers page.`)
  await expect.element(alert.getByRole('link', { name: 'Model providers' })).toHaveAttribute('href', '/model-providers')
})

test('while the account check runs, the alert leaves out the last sentence and shows no link', async () => {
  // Arrange
  const membershipPath = `/api/v1/users/by-identifier/${member.identifier}`
  await commands.holdRequests(membershipPath)
  onTestFinished(() => commands.releaseRequests(membershipPath))
  const screen = await renderCard([...signedInMember, failedOnAnUnusableAnswer])

  // Assert
  const alert = screen.getByRole('alert')
  await expect.element(alert.getByText(unusableAnswer, { exact: true })).toBeVisible()
  await expect.element(alert.getByRole('link')).not.toBeInTheDocument()
})

test('when the account check fails, the alert gives the words for a member and shows no link', async () => {
  // Arrange
  const refused = { method: 'GET', path: `/api/v1/users/by-identifier/${member.identifier}`, status: 403, body: { type: 'about:blank', title: 'Forbidden', status: 403, detail: 'You cannot use this tenant.' } } as const
  const screen = await renderCard([...signedInMember, refused, failedOnAnUnusableAnswer])

  // Assert
  const alert = screen.getByRole('alert')
  await expect.element(alert).toHaveTextContent(forAMember)
  await expect.element(alert.getByRole('link')).not.toBeInTheDocument()
})

const tagged = (before: string, label: string) =>
  JSON.stringify({
    type: 'doc',
    content: [{ type: 'paragraph', content: [{ type: 'text', text: before }, { type: 'mention', attrs: { personId: 'per_1', label } }] }],
  })

test('a waiting result that does not tag a Person whom Cleaned tags says so before the use', async () => {
  // Arrange
  const screen = await renderCard([
    { method: 'GET', path: '/api/v1/sessions/ses_1', body: waitingSince({ cleanedText: tagged('I met ', 'Zed Renamed') }) },
    { method: 'GET', path: '/api/v1/sessions/ses_1/cleaned-revisions/3', body: revision(3, doc('I met Zed')) },
  ])

  // Assert
  await expect.element(screen.getByText('Cleaned tags Zed Renamed, and this result does not. If you use it, Cleaned loses that tag.')).toBeVisible()
})

test('a waiting result that does not load keeps the use off and says why', async () => {
  // Arrange
  const screen = await renderCard([
    { method: 'GET', path: '/api/v1/sessions/ses_1', body: waitingSince({ cleanedText: tagged('I met ', 'Zed') }) },
    { method: 'GET', path: '/api/v1/sessions/ses_1/cleaned-revisions/3', status: 500, body: { status: 500 } },
  ])

  // Assert
  await expect.element(screen.getByText('The result did not load, so the page cannot say which tags a use keeps.')).toBeVisible()
  await expect.element(screen.getByText('The Server could not do this, and it answered with error 500. Try again later.')).toBeVisible()
  await expect.element(screen.getByRole('button', { name: 'Use the Cleanup result' })).toBeDisabled()
})

test('a waiting result that keeps each tag of Cleaned says nothing of tags', async () => {
  // Arrange
  const screen = await renderCard([
    { method: 'GET', path: '/api/v1/sessions/ses_1', body: waitingSince({ cleanedText: tagged('I met ', 'Zed') }) },
    { method: 'GET', path: '/api/v1/sessions/ses_1/cleaned-revisions/3', body: revision(3, tagged('Today I met ', 'Zed')) },
  ])

  // Assert
  await expect.element(screen.getByRole('button', { name: 'Use the Cleanup result' })).toBeEnabled()
  await expect.element(screen.getByText(/Cleaned tags/)).not.toBeInTheDocument()
})

const runsNow = 'A new Cleanup of this Session runs now, and its result can replace this one. Look again when it ends.'
const refusal = (detail: string) => ({
  status: 409,
  body: { type: 'https://tools.ietf.org/html/rfc9110#section-15.5.10', title: 'Conflict', status: 409, detail, errors: { cleanup: [detail] } },
})

test('a use that a run refused stops saying that the run goes on when the run ends', async () => {
  // Arrange: another tab started a run after this page read the waiting result.
  const answersWith = (body: SessionDto) => [
    { method: 'GET', path: '/api/v1/model-providers/active', body: { name: 'Mine' } },
    { method: 'GET', path: '/api/v1/sessions/ses_1', body },
    { method: 'GET', path: '/api/v1/sessions/ses_1/cleaned-revisions/3', body: revision(3, doc('second clean words')) },
    { method: 'POST', path: '/api/v1/sessions/ses_1/cleanup/waiting/apply', ...refusal(runsNow) },
  ] satisfies Parameters<typeof commands.answerRequests>[0]
  const screen = await renderCard(answersWith(waitingSince({})))
  await expect.element(screen.getByRole('button', { name: 'Use the Cleanup result' })).toBeEnabled()
  await commands.answerRequests(answersWith(waitingSince({ cleanupStatus: 'Running' })))
  await userEvent.click(screen.getByRole('button', { name: 'Use the Cleanup result' }))
  await expect.element(screen.getByText(runsNow)).toBeVisible()

  // Act
  await commands.answerRequests(answersWith(session({ cleanedDraftRevision: 4, cleanedLatestRevision: 4, cleanupBaseCleanedRevision: 2 })))

  // Assert
  await expect.element(screen.getByRole('status'), { timeout: 5_000 }).toHaveTextContent('Cleaned is current.')
  await expect.element(screen.getByText(runsNow)).not.toBeInTheDocument()
})

test('a new run clears the refusal of a use', async () => {
  // Arrange
  const stale = 'Cleaned changed after you read it. Look at Cleaned as it is now, and then choose again.'
  const answersWith = (body: SessionDto) => [
    { method: 'GET', path: '/api/v1/model-providers/active', body: { name: 'Mine' } },
    { method: 'GET', path: '/api/v1/sessions/ses_1', body },
    { method: 'GET', path: '/api/v1/sessions/ses_1/cleaned-revisions/3', body: revision(3, doc('second clean words')) },
    { method: 'POST', path: '/api/v1/sessions/ses_1/cleanup/waiting/apply', ...refusal(stale) },
    { method: 'POST', path: '/api/v1/sessions/ses_1/cleanup', body: waitingSince({ cleanupStatus: 'Running' }) },
  ] satisfies Parameters<typeof commands.answerRequests>[0]
  const screen = await renderCard(answersWith(waitingSince({})))
  await expect.element(screen.getByRole('button', { name: 'Use the Cleanup result' })).toBeEnabled()
  await userEvent.click(screen.getByRole('button', { name: 'Use the Cleanup result' }))
  await expect.element(screen.getByText(stale)).toBeVisible()
  await commands.answerRequests(answersWith(waitingSince({ cleanupStatus: 'Running' })))

  // Act
  await userEvent.click(screen.getByRole('button', { name: 'Run Cleanup again' }))

  // Assert
  await expect.element(screen.getByRole('button', { name: 'Stop Cleanup' })).toBeVisible()
  await expect.element(screen.getByText(stale)).not.toBeInTheDocument()
})

test('a hand edit after a Cleanup shows one status, and it does not say that Cleaned is current', async () => {
  // Arrange
  const screen = await renderCard([{ method: 'GET', path: '/api/v1/sessions/ses_1', body: session({ cleanedHasHandEdits: true, cleanedDraftRevision: 2, cleanedLatestRevision: 2 }) }])

  // Assert
  await expect.element(screen.getByRole('status')).toHaveTextContent('You edited Cleaned by hand after the last Cleanup.')
  await expect.element(screen.getByText('A new Cleanup replaces Cleaned, and your edit stays as a Revision.')).toBeVisible()
  await expect.element(screen.getByText('Cleaned is current.')).not.toBeInTheDocument()
})

for (const width of [390, 1280]) {
  test(`at ${width} px, the kind of a suggestion comes before its value on the same line`, async () => {
    // Arrange
    await page.viewport(width, 900)
    const screen = await renderCard([
      { method: 'GET', path: '/api/v1/sessions/ses_1', body: session({ suggestions: [{ kind: 'Topic', value: 'work' }] }) },
    ])

    await expect.element(screen.getByText('work', { exact: true })).toBeVisible()

    // Act
    const chip = screen.getByText('Topic', { exact: true }).element().getBoundingClientRect()
    const value = screen.getByText('work', { exact: true }).element().getBoundingClientRect()

    // Assert
    expect(chip.right).toBeLessThanOrEqual(value.left)
    expect(Math.abs(chip.top + chip.height / 2 - (value.top + value.height / 2))).toBeLessThan(4)
  })
}

const janePark = { id: 'per_1', label: 'Jane', fullName: 'Jane Park', notes: "Sam's teacher", version: 2 }
const janeDoe = { id: 'per_2', label: 'Jane', fullName: 'Jane Doe', notes: null, version: 2 }
const proposalOf = (...matchedPersonIds: string[]): Partial<SessionDto> => ({
  peopleProposals: [{ label: 'Jane', matchedPersonIds, isNew: matchedPersonIds.length === 0, contexts: [] }],
})
const respondPath = '/api/v1/sessions/ses_1/people-proposals/respond'
const sentResponses = async () =>
  (await commands.takeSentRequests()).filter((request) => request.path === respondPath).map((request) => request.body)

test('a proposal of two people of one name shows the details of each, and a Tag tags the one that the person chose', async () => {
  const screen = await renderCard([
    { method: 'GET', path: '/api/v1/sessions/ses_1', body: session(proposalOf(janePark.id, janeDoe.id)) },
    { method: 'GET', path: '/api/v1/people/per_1', body: janePark },
    { method: 'GET', path: '/api/v1/people/per_2', body: janeDoe },
    { method: 'POST', path: respondPath, body: session({}) },
  ])
  const choices = screen.getByRole('list', { name: 'People named Jane' })
  await expect.element(choices.getByText("Sam's teacher")).toBeVisible()
  await expect.element(choices.getByText('Jane Doe')).toBeVisible()
  await expect.element(screen.getByRole('button', { name: 'Tag Jane', exact: true })).not.toBeInTheDocument()
  await commands.takeSentRequests()

  await userEvent.click(screen.getByRole('button', { name: 'Tag Jane Doe' }))

  await expect.element(screen.getByText('People to tag')).not.toBeInTheDocument()
  expect(await sentResponses()).toEqual([{ label: 'Jane', approve: true, createNew: false, bindToPersonId: 'per_2' }])
})

test('a proposal of two people of one name can add a new person', async () => {
  const screen = await renderCard([
    { method: 'GET', path: '/api/v1/sessions/ses_1', body: session(proposalOf(janePark.id, janeDoe.id)) },
    { method: 'GET', path: '/api/v1/people/per_1', body: janePark },
    { method: 'GET', path: '/api/v1/people/per_2', body: janeDoe },
    { method: 'POST', path: respondPath, body: session({}) },
  ])
  await expect.element(screen.getByText('Jane Doe')).toBeVisible()
  await commands.takeSentRequests()

  await userEvent.click(screen.getByRole('button', { name: 'Add a new person named Jane' }))

  await expect.element(screen.getByText('People to tag')).not.toBeInTheDocument()
  expect(await sentResponses()).toEqual([{ label: 'Jane', approve: true, createNew: true }])
})

test('a proposal of one match shows its details, and a Tag leaves the choice to the Server', async () => {
  const screen = await renderCard([
    { method: 'GET', path: '/api/v1/sessions/ses_1', body: session(proposalOf(janePark.id)) },
    { method: 'GET', path: '/api/v1/people/per_1', body: janePark },
    { method: 'POST', path: respondPath, body: session({}) },
  ])
  await expect.element(screen.getByText('Jane Park')).toBeVisible()
  await commands.takeSentRequests()

  await userEvent.click(screen.getByRole('button', { name: 'Tag Jane', exact: true }))

  await expect.element(screen.getByText('People to tag')).not.toBeInTheDocument()
  expect(await sentResponses()).toEqual([{ label: 'Jane', approve: true, createNew: false }])
})

test('a match with no details says where to add them', async () => {
  const screen = await renderCard([
    { method: 'GET', path: '/api/v1/sessions/ses_1', body: session(proposalOf(janePark.id, janeDoe.id)) },
    { method: 'GET', path: '/api/v1/people/per_1', body: janePark },
    { method: 'GET', path: '/api/v1/people/per_2', body: { ...janeDoe, fullName: null } },
  ])

  const choices = screen.getByRole('list', { name: 'People named Jane' })

  await expect.element(choices.getByText('No details. Add them on the People page.')).toBeVisible()
  await expect.element(choices.getByRole('button', { name: 'Tag Jane, no details' })).toBeVisible()
})

test('a Tag that the Server refuses because two people have the name shows why, and the proposal stays', async () => {
  const askWhich = 'Two or more People have this name. Choose the person to tag, or add a new person.'
  const screen = await renderCard([
    { method: 'GET', path: '/api/v1/sessions/ses_1', body: session(proposalOf(janePark.id)) },
    { method: 'GET', path: '/api/v1/people/per_1', body: janePark },
    {
      method: 'POST',
      path: respondPath,
      status: 422,
      body: { type: 'https://tools.ietf.org/html/rfc4918#section-11.2', title: 'Unprocessable Entity', status: 422, detail: askWhich, errors: { bindToPersonId: [askWhich] } },
    },
  ])
  await expect.element(screen.getByText('Jane Park')).toBeVisible()

  await userEvent.click(screen.getByRole('button', { name: 'Tag Jane', exact: true }))

  await expect.element(screen.getByRole('alert')).toHaveTextContent(askWhich)
  await expect.element(screen.getByRole('button', { name: 'Add a new person named Jane' })).toBeVisible()
})
