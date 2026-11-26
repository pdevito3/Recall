import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { createMemoryHistory, createRouter, RouterProvider } from '@tanstack/react-router'
import { afterEach, expect, test } from 'vitest'
import { commands, userEvent } from 'vitest/browser'
import { render } from 'vitest-browser-react'

import type { ImportBatchDto } from '@/api/generated/models/types/import-batch-dto'
import type { ImportDayDto } from '@/api/generated/models/types/import-day-dto'
import type { ImportEntryDto } from '@/api/generated/models/types/import-entry-dto'
import { toastQueue } from '@/components/notification'
import { Toaster } from '@/components/toaster'
import { routeTree } from '@/routeTree.gen'
import type { Answer } from '@/test/answer'
import { memberSetting, signedInMember } from '@/test/session'

import { ImportReviewSetting } from './import-review-setting'

const batchesPath = '/api/v1/import-batches'
const daysPath = '/api/v1/import-batches/imp_1/days'
const entriesPath = '/api/v1/import-batches/imp_1/entries'

const documentOf = (text: string) => JSON.stringify({ type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text }] }] })

const diary: ImportBatchDto = {
  id: 'imp_1',
  name: 'Diary 2024',
  timeZone: 'America/New_York',
  state: 'Open',
  openedAt: '2026-11-20T10:00:00Z',
  acceptedAt: null,
  entryCount: 3,
  result: null,
  version: 4,
}

const coffee: ImportEntryDto = {
  clientSessionId: 'c1',
  date: '2024-03-01',
  position: 0,
  time: '08:30',
  timeZone: null,
  raw: 'Coffee on the porch.',
  document: documentOf('Coffee on the porch.'),
  moods: ['Calm'],
  topics: ['Morning'],
  review: { rejected: false, words: null, time: null },
  isRejected: false,
  startsAt: '2024-03-01T13:30:00Z',
  wordsChangedAfterEdit: false,
  timeChangedAfterEdit: false,
}

const walk: ImportEntryDto = {
  ...coffee,
  clientSessionId: 'c2',
  position: 1,
  time: null,
  raw: 'Long walk.',
  document: documentOf('Long walk.'),
  moods: [],
  topics: [],
  startsAt: '2024-03-01T17:01:00Z',
}

const london: ImportEntryDto = {
  ...coffee,
  clientSessionId: 'c3',
  date: '2024-03-02',
  time: '21:00',
  timeZone: 'Europe/London',
  raw: 'Flew to London.',
  document: documentOf('Flew to London.'),
  moods: [],
  topics: [],
  startsAt: '2024-03-02T21:00:00Z',
}

const dayOf = (date: string, entryCount: number, patch: Partial<ImportDayDto> = {}): ImportDayDto => ({ date, entryCount, rejectedCount: 0, rejected: false, ...patch })

const twoDays = [dayOf('2024-03-01', 2), dayOf('2024-03-02', 1)]

const batchesAre = (batches: ImportBatchDto[]): Answer => ({ method: 'GET', path: batchesPath, body: batches })
const daysAre = (days: ImportDayDto[], version = 4): Answer => ({ method: 'GET', path: daysPath, body: { version, days } })
const entriesAre = (entries: ImportEntryDto[], from = '2024-03-01', to = '2024-03-02'): Answer => ({
  method: 'GET',
  path: entriesPath,
  query: { from, to },
  body: { version: 4, entries, rejectedDays: [] },
})

const problem = (status: number, detail: string, type = 'about:blank') => ({ type, title: 'Refused', status, detail })

afterEach(() => toastQueue.clear())

const noPeople: Answer = {
  method: 'GET',
  path: '/api/v1/people',
  headers: { 'x-pagination': JSON.stringify({ totalCount: 0, pageSize: 100, currentPageSize: 0, currentStartIndex: 0, currentEndIndex: 0, pageNumber: 1, totalPages: 1, hasPrevious: false, hasNext: false }) },
  body: [],
}

async function renderAt(path: string, answers: Answer[]) {
  await commands.answerRequests([...signedInMember, noPeople, ...answers])
  const router = createRouter({ routeTree, history: createMemoryHistory({ initialEntries: [path] }) })
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const screen = await render(
    <QueryClientProvider client={client}>
      <RouterProvider router={router} />
      <Toaster />
    </QueryClientProvider>,
  )
  return { screen, router }
}

async function renderReview() {
  const rendered = await renderAt('/imports/imp_1', [batchesAre([diary]), daysAre(twoDays), entriesAre([coffee, walk, london])])
  await expect.element(rendered.screen.getByRole('listitem', { name: 'Entry at 9:00 PM' })).toBeVisible()
  return rendered
}

async function renderSetting() {
  await commands.answerRequests(signedInMember)
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <ImportReviewSetting />
    </QueryClientProvider>,
  )
}

const cardOf = (screen: Awaited<ReturnType<typeof renderReview>>['screen'], time: string) => screen.getByRole('listitem', { name: `Entry at ${time}` })

test('@IMP-33 the list shows each batch with its name, zone, count, state, and the time it opened in the browser zone', async () => {
  // Arrange
  const accepted: ImportBatchDto = { ...diary, id: 'imp_2', name: 'Diary 2023', state: 'Accepted', acceptedAt: '2026-11-21T10:00:00Z', entryCount: 1 }

  // Act
  const { screen } = await renderAt('/imports', [batchesAre([diary, accepted])])

  // Assert
  const open = screen.getByRole('listitem', { name: 'Diary 2024' })
  await expect.element(open).toMatchTextContent('Diary 2024Open3 entries in Eastern TimeOpened on Nov 21, 2026, 12:00 AM.')
  const done = screen.getByRole('listitem', { name: 'Diary 2023' })
  await expect.element(done).toMatchTextContent('Diary 2023In your journal1 entry in Eastern TimeOpened on Nov 21, 2026, 12:00 AM. Accepted on Nov 22, 2026, 12:00 AM.')
  await expect.element(screen.getByRole('link', { name: 'Diary 2024' })).toHaveAttribute('href', '/imports/imp_1')
})

test('@IMP-33 with no batch, the list says that an agent opens one with a personal access token', async () => {
  // Arrange and Act
  const { screen } = await renderAt('/imports', [batchesAre([])])

  // Assert
  await expect.element(screen.getByText(/^You have no import batches\. An agent opens a batch with a personal access token, then you review it here\.$/)).toBeVisible()
  await expect.element(screen.getByRole('link', { name: 'personal access token' })).toHaveAttribute('href', '/settings/personal-access-tokens')
})

test('@IMP-34 discard asks with the name and the count, sends the delete, and the batch leaves the list', async () => {
  // Arrange
  const { screen } = await renderAt('/imports', [batchesAre([diary])])
  await userEvent.click(screen.getByRole('button', { name: 'Discard Diary 2024' }))
  await expect.element(screen.getByRole('alertdialog', { name: 'Discard Diary 2024?' })).toMatchTextContent('This batch and its 3 entries leave Recall')
  await commands.answerRequests([...signedInMember, noPeople, batchesAre([]), { method: 'DELETE', path: '/api/v1/import-batches/imp_1', status: 204 }])
  await commands.takeSentRequests()

  // Act
  await userEvent.click(screen.getByRole('button', { name: 'Discard batch' }))

  // Assert
  await expect.element(screen.getByText('Batch discarded')).toBeVisible()
  await expect.element(screen.getByRole('listitem', { name: 'Diary 2024' })).not.toBeInTheDocument()
  expect(await commands.takeSentRequests()).toEqual([{ method: 'DELETE', path: '/api/v1/import-batches/imp_1', body: null }])
})

test('@IMP-34 an accepted batch has no Discard', async () => {
  // Arrange and Act
  const { screen } = await renderAt('/imports', [batchesAre([{ ...diary, state: 'Accepted' }])])

  // Assert
  await expect.element(screen.getByRole('listitem', { name: 'Diary 2024' })).toMatchTextContent('In your journal')
  await expect.element(screen.getByRole('button', { name: 'Discard Diary 2024' })).not.toBeInTheDocument()
})

test('@IMP-35 the review reads a page of whole days with from and to, and Next reads the next page', async () => {
  // Arrange
  const days = [dayOf('2024-03-01', 150), dayOf('2024-03-02', 100)]
  const { screen, router } = await renderAt('/imports/imp_1', [
    batchesAre([diary]),
    daysAre(days),
    entriesAre([coffee], '2024-03-01', '2024-03-01'),
    entriesAre([london], '2024-03-02', '2024-03-02'),
  ])
  await expect.element(cardOf(screen, '8:30 AM')).toBeVisible()
  await expect.element(screen.getByRole('heading', { level: 2, name: 'Saturday, March 2, 2024' })).not.toBeInTheDocument()

  // Act
  await userEvent.click(screen.getByRole('navigation', { name: 'Pages of this batch', exact: true }).getByRole('button', { name: 'Next' }))

  // Assert
  await expect.element(cardOf(screen, '9:00 PM')).toBeVisible()
  await expect.element(screen.getByRole('heading', { level: 2, name: 'Friday, March 1, 2024' })).not.toBeInTheDocument()
  expect(router.state.location.search).toEqual({ page: 2 })
  expect(await commands.takeUnansweredRequests()).toEqual([])
})

test('@IMP-35 the entries show in groups by day, in the order of the date and the position', async () => {
  // Arrange and Act
  const { screen } = await renderReview()

  // Assert
  const first = screen.getByRole('list', { name: 'Entries of Friday, March 1, 2024' })
  await expect.element(first.getByRole('listitem', { name: /^Entry at / }).first()).toHaveAccessibleName('Entry at 8:30 AM')
  await expect.element(first.getByRole('listitem', { name: /^Entry at / }).nth(1)).toHaveAccessibleName('Entry at 12:01 PM')
  await expect.element(screen.getByRole('list', { name: 'Entries of Saturday, March 2, 2024' }).getByRole('listitem', { name: /^Entry at / })).toHaveAccessibleName('Entry at 9:00 PM')
})

test('@IMP-35 a rejected day with no entry shows as a day', async () => {
  // Arrange and Act
  const { screen } = await renderAt('/imports/imp_1', [
    batchesAre([diary]),
    daysAre([dayOf('2024-03-01', 0, { rejected: true })]),
    entriesAre([], '2024-03-01', '2024-03-01'),
  ])

  // Assert
  await expect.element(screen.getByRole('heading', { level: 2, name: 'Friday, March 1, 2024' })).toBeVisible()
  await expect.element(screen.getByText('This day has no entries. While it is rejected, each entry that the import adds to it stays out.')).toBeVisible()
})

test('@IMP-36 each entry shows its start time in its zone, the zone when it is not the zone of the batch, its words, Moods, and Topics', async () => {
  // Arrange and Act
  const { screen } = await renderReview()

  // Assert
  await expect.element(cardOf(screen, '8:30 AM')).toMatchTextContent('8:30 AMCoffee on the porch.CalmMorning')
  await expect.element(cardOf(screen, '8:30 AM').getByRole('list', { name: 'Moods and topics' })).toMatchTextContent('CalmMorning')
  await expect.element(cardOf(screen, '9:00 PM')).toMatchTextContent('9:00 PMUnited Kingdom TimeFlew to London.')
})

test('@IMP-36 an entry with no time of the person or the agent says that its time comes from its place in the day', async () => {
  // Arrange and Act
  const { screen } = await renderReview()

  // Assert
  await expect.element(cardOf(screen, '12:01 PM')).toMatchTextContent('12:01 PMTime from its place in the dayLong walk.')
  await expect.element(cardOf(screen, '8:30 AM')).not.toMatchTextContent('Time from its place')
})

test('@IMP-37 an entry whose words or time the import changed after the edit says that the accept keeps the edit', async () => {
  // Arrange
  const flagged = { ...coffee, review: { rejected: false, words: documentOf('Mine.'), time: '09:00' }, wordsChangedAfterEdit: true, timeChangedAfterEdit: true }

  // Act
  const { screen } = await renderAt('/imports/imp_1', [batchesAre([diary]), daysAre(twoDays), entriesAre([flagged, walk, london])])

  // Assert
  const card = cardOf(screen, '8:30 AM')
  await expect.element(card.getByText('The import changed the words after your edit. The accept keeps your edit.')).toBeVisible()
  await expect.element(card.getByText('The import changed the time after your edit. The accept keeps your time.')).toBeVisible()
  await expect.element(cardOf(screen, '12:01 PM').getByText(/The import changed/)).not.toBeInTheDocument()
})

test('@IMP-38 a save of a new time sends the whole review with the words of the last edit, and the entry shows the reply', async () => {
  // Arrange
  const { screen } = await renderReview()
  await userEvent.click(screen.getByRole('button', { name: 'Edit the entry at 8:30 AM' }))
  const form = screen.getByRole('form', { name: 'Edit the entry' })
  await userEvent.click(form.getByRole('spinbutton', { name: /^hour,/ }))
  await userEvent.keyboard('0915')
  const reply = { ...coffee, review: { rejected: false, words: null, time: '09:15' }, startsAt: '2024-03-01T14:15:00Z' }
  await commands.answerRequests([
    ...signedInMember,
    noPeople,
    batchesAre([diary]),
    daysAre(twoDays, 5),
    entriesAre([reply, walk, london]),
    { method: 'PUT', path: '/api/v1/import-batches/imp_1/entries/review', body: { version: 5, entry: reply } },
  ])
  await commands.takeSentRequests()

  // Act
  await userEvent.click(form.getByRole('button', { name: 'Save' }))

  // Assert
  await expect.element(cardOf(screen, '9:15 AM')).toMatchTextContent('9:15 AMEdited')
  await expect.element(screen.getByRole('button', { name: 'Edit the entry at 9:15 AM' })).toHaveFocus()
  expect(await commands.takeSentRequests()).toEqual([
    { method: 'PUT', path: '/api/v1/import-batches/imp_1/entries/review', body: { clientSessionId: 'c1', rejected: false, words: null, time: '09:15' } },
  ])
})

test('@IMP-38 Use the import words sends null for the words and keeps the time of the last edit', async () => {
  // Arrange
  const edited = { ...coffee, review: { rejected: false, words: documentOf('Mine.'), time: '09:00' }, document: documentOf('Mine.') }
  const { screen } = await renderAt('/imports/imp_1', [batchesAre([diary]), daysAre(twoDays), entriesAre([edited, walk, london])])
  await userEvent.click(screen.getByRole('button', { name: /^Edit the entry at / }).first())
  await commands.answerRequests([
    ...signedInMember,
    noPeople,
    batchesAre([diary]),
    daysAre(twoDays, 5),
    entriesAre([coffee, walk, london]),
    { method: 'PUT', path: '/api/v1/import-batches/imp_1/entries/review', body: { version: 5, entry: coffee } },
  ])
  await commands.takeSentRequests()

  // Act
  await userEvent.click(screen.getByRole('button', { name: 'Use the import words' }))

  // Assert
  await expect.element(screen.getByRole('form', { name: 'Edit the entry' })).not.toBeInTheDocument()
  expect(await commands.takeSentRequests()).toEqual([
    { method: 'PUT', path: '/api/v1/import-batches/imp_1/entries/review', body: { clientSessionId: 'c1', rejected: false, words: null, time: '09:00' } },
  ])
})

test('@IMP-38 a refused edit shows the message of the Server in the form, and the form keeps the change', async () => {
  // Arrange
  const { screen } = await renderReview()
  await userEvent.click(screen.getByRole('button', { name: 'Edit the entry at 8:30 AM' }))
  const form = screen.getByRole('form', { name: 'Edit the entry' })
  await userEvent.click(form.getByRole('spinbutton', { name: /^hour,/ }))
  await userEvent.keyboard('1100')
  const refusal = { ...problem(422, 'The time of an entry cannot be in the future.'), errors: { time: ['The time of an entry cannot be in the future.'] } }
  await commands.answerRequests([...signedInMember, noPeople, batchesAre([diary]), daysAre(twoDays), entriesAre([coffee, walk, london]), { method: 'PUT', path: '/api/v1/import-batches/imp_1/entries/review', status: 422, body: refusal }])

  // Act
  await userEvent.click(form.getByRole('button', { name: 'Save' }))

  // Assert
  await expect.element(form.getByRole('alert')).toMatchTextContent('The time of an entry cannot be in the future.')
  await expect.poll(() => form.getByRole('group', { name: 'Time' }).element().textContent).toMatch(/11\W*00\W*AM/)
  await expect.element(screen.getByText('The time of an entry cannot be in the future.', { exact: true }).first()).toBeVisible()
  expect(toastQueue.visibleToasts).toEqual([])
})

test('@IMP-39 Reject sends the whole review with rejected, and the entry then says Rejected with Keep', async () => {
  // Arrange
  const { screen } = await renderReview()
  const rejected = { ...walk, review: { rejected: true, words: null, time: null }, isRejected: true }
  await commands.answerRequests([
    ...signedInMember,
    noPeople,
    batchesAre([diary]),
    daysAre([dayOf('2024-03-01', 2, { rejectedCount: 1 }), dayOf('2024-03-02', 1)], 5),
    entriesAre([coffee, rejected, london]),
    { method: 'PUT', path: '/api/v1/import-batches/imp_1/entries/review', body: { version: 5, entry: rejected } },
  ])
  await commands.takeSentRequests()

  // Act
  await userEvent.click(screen.getByRole('button', { name: 'Reject the entry at 12:01 PM' }))

  // Assert
  await expect.element(cardOf(screen, '12:01 PM')).toMatchTextContent('Rejected')
  await expect.element(screen.getByRole('button', { name: 'Keep the entry at 12:01 PM' })).toBeVisible()
  expect(await commands.takeSentRequests()).toEqual([
    { method: 'PUT', path: '/api/v1/import-batches/imp_1/entries/review', body: { clientSessionId: 'c2', rejected: true, words: null, time: null } },
  ])
})

test('@IMP-40 Reject day sends the review of the day, and each entry of the day shows Rejected with its day and no Keep', async () => {
  // Arrange
  const { screen } = await renderReview()
  const held = (entry: ImportEntryDto) => ({ ...entry, isRejected: true })
  await commands.answerRequests([
    ...signedInMember,
    noPeople,
    batchesAre([diary]),
    daysAre([dayOf('2024-03-01', 2, { rejected: true }), dayOf('2024-03-02', 1)], 5),
    entriesAre([held(coffee), held(walk), london]),
    { method: 'PUT', path: '/api/v1/import-batches/imp_1/days/review', body: { version: 5, date: '2024-03-01', rejected: true } },
  ])
  await commands.takeSentRequests()

  // Act
  await userEvent.click(screen.getByRole('button', { name: 'Reject Friday, March 1, 2024' }))

  // Assert
  await expect.element(cardOf(screen, '8:30 AM')).toMatchTextContent('Rejected with its day')
  await expect.element(cardOf(screen, '12:01 PM')).toMatchTextContent('Rejected with its day')
  await expect.element(cardOf(screen, '8:30 AM').getByRole('button', { name: /^(Keep|Reject) the entry/ })).not.toBeInTheDocument()
  await expect.element(screen.getByRole('button', { name: 'Keep Friday, March 1, 2024' })).toBeVisible()
  expect(await commands.takeSentRequests()).toEqual([{ method: 'PUT', path: '/api/v1/import-batches/imp_1/days/review', body: { date: '2024-03-01', rejected: true } }])
})

test('@IMP-40 an entry that the person rejected alone stays rejected, with Keep, when its day is kept again', async () => {
  // Arrange
  const alone = { ...walk, review: { rejected: true, words: null, time: null }, isRejected: true }

  // Act
  const { screen } = await renderAt('/imports/imp_1', [batchesAre([diary]), daysAre([dayOf('2024-03-01', 2, { rejectedCount: 1 }), dayOf('2024-03-02', 1)]), entriesAre([coffee, alone, london])])

  // Assert
  await expect.element(cardOf(screen, '12:01 PM')).toMatchTextContent('Rejected')
  await expect.element(cardOf(screen, '12:01 PM')).not.toMatchTextContent('with its day')
  await expect.element(screen.getByRole('button', { name: 'Keep the entry at 12:01 PM' })).toBeVisible()
})

test('@IMP-41 the counts come from the days, and a review reads the days again', async () => {
  // Arrange
  const { screen } = await renderReview()
  await expect.element(screen.getByLabelText('Counts of this batch')).toMatchTextContent('3 entries: 3 kept, 0 rejected')
  const rejected = { ...walk, review: { rejected: true, words: null, time: null }, isRejected: true }
  await commands.answerRequests([
    ...signedInMember,
    noPeople,
    batchesAre([diary]),
    daysAre([dayOf('2024-03-01', 2, { rejectedCount: 1 }), dayOf('2024-03-02', 1, { rejected: true })], 6),
    entriesAre([coffee, rejected, london]),
    { method: 'PUT', path: '/api/v1/import-batches/imp_1/entries/review', body: { version: 6, entry: rejected } },
  ])

  // Act
  await userEvent.click(screen.getByRole('button', { name: 'Reject the entry at 12:01 PM' }))

  // Assert
  await expect.element(screen.getByLabelText('Counts of this batch')).toMatchTextContent('3 entries: 1 kept, 2 rejected')
})

test('@IMP-42 Accept asks with the kept and rejected counts, and sends the version of the last read of the days', async () => {
  // Arrange
  const { screen } = await renderAt('/imports/imp_1', [
    batchesAre([diary]),
    daysAre([dayOf('2024-03-01', 2, { rejectedCount: 1 }), dayOf('2024-03-02', 1)], 9),
    entriesAre([coffee, walk, london]),
  ])
  await userEvent.click(screen.getByRole('button', { name: 'Accept', exact: true }))
  const dialog = screen.getByRole('alertdialog', { name: 'Accept this batch?' })
  await expect.element(dialog).toMatchTextContent('2 entries go into your journal, and 1 entry that you rejected stay out.')
  const accepted: ImportBatchDto = {
    ...diary,
    state: 'Accepted',
    acceptedAt: '2026-11-25T10:00:00Z',
    result: { sessionsMade: 2, entriesRejected: 1, daysRejected: 0, skippedInJournal: 0, skippedDeleted: 0 },
  }
  await commands.answerRequests([...signedInMember, noPeople, batchesAre([accepted]), { method: 'POST', path: '/api/v1/import-batches/imp_1/accept', body: accepted }])
  await commands.takeSentRequests()

  // Act
  await userEvent.click(dialog.getByRole('button', { name: 'Accept batch' }))

  // Assert
  await expect.element(screen.getByRole('list', { name: 'Result of the accept' })).toBeVisible()
  expect(await commands.takeSentRequests()).toEqual([{ method: 'POST', path: '/api/v1/import-batches/imp_1/accept', body: { version: 9 } }])
})

test('@IMP-43 an accepted batch shows the counts of its result and a link to the journal, and no review action', async () => {
  // Arrange
  const accepted: ImportBatchDto = {
    ...diary,
    state: 'Accepted',
    acceptedAt: '2026-11-25T10:00:00Z',
    result: { sessionsMade: 12, entriesRejected: 3, daysRejected: 1, skippedInJournal: 2, skippedDeleted: 1 },
  }

  // Act
  const { screen } = await renderAt('/imports/imp_1', [batchesAre([accepted])])

  // Assert
  await expect.element(screen.getByText('This batch is in your journal. Accepted on Nov 26, 2026, 12:00 AM.')).toBeVisible()
  const lines = screen.getByRole('list', { name: 'Result of the accept' }).getByRole('listitem')
  expect(lines.elements().map((line) => line.textContent)).toEqual([
    '12 entries made in your journal',
    '3 entries rejected',
    '1 day rejected',
    '2 entries skipped, because your journal has them',
    '1 entry skipped, because you deleted them',
  ])
  await expect.element(screen.getByRole('link', { name: 'Go to your journal' })).toHaveAttribute('href', '/')
  await expect.element(screen.getByRole('button', { name: 'Accept', exact: true })).not.toBeInTheDocument()
  await expect.element(screen.getByRole('heading', { level: 1, name: 'Diary 2024' })).toHaveFocus()
})

test('@IMP-44 a 409 of a changed batch keeps the dialog open with the message, and the dialog shows the counts of a new read', async () => {
  // Arrange
  const { screen } = await renderReview()
  await userEvent.click(screen.getByRole('button', { name: 'Accept', exact: true }))
  const dialog = screen.getByRole('alertdialog', { name: 'Accept this batch?' })
  await expect.element(dialog).toMatchTextContent('3 entries go into your journal')
  const changed = 'This batch changed after you read it. Read it again, then accept it.'
  await commands.answerRequests([
    ...signedInMember,
    noPeople,
    batchesAre([diary]),
    daysAre([...twoDays, dayOf('2024-03-03', 1)], 7),
    entriesAre([coffee, walk, london]),
    entriesAre([coffee, walk, london], '2024-03-01', '2024-03-03'),
    { method: 'POST', path: '/api/v1/import-batches/imp_1/accept', status: 409, body: { ...problem(409, changed, 'urn:recall:problem:stale-version'), errors: { version: [changed] } } },
  ])
  await commands.takeSentRequests()

  // Act
  await userEvent.click(dialog.getByRole('button', { name: 'Accept batch' }))

  // Assert
  await expect.element(dialog.getByRole('alert')).toMatchTextContent(changed)
  await expect.element(dialog).toMatchTextContent('4 entries go into your journal')
  await commands.answerRequests([
    ...signedInMember,
    noPeople,
    batchesAre([diary]),
    daysAre([...twoDays, dayOf('2024-03-03', 1)], 7),
    entriesAre([coffee, walk, london], '2024-03-01', '2024-03-03'),
    { method: 'POST', path: '/api/v1/import-batches/imp_1/accept', status: 409, body: problem(409, changed, 'urn:recall:problem:stale-version') }])
  await userEvent.click(dialog.getByRole('button', { name: 'Accept batch' }))
  await expect.element(dialog.getByRole('alert')).toMatchTextContent(changed)
  expect((await commands.takeSentRequests()).map((r) => r.body)).toEqual([{ version: 4 }, { version: 7 }])
})

test('@IMP-44 a 409 of a review of a batch that an accept closed shows the message, and the page reads the batch again', async () => {
  // Arrange
  const { screen } = await renderReview()
  const closed = 'This batch is in your journal, so it cannot change.'
  const accepted: ImportBatchDto = { ...diary, state: 'Accepted', result: { sessionsMade: 3, entriesRejected: 0, daysRejected: 0, skippedInJournal: 0, skippedDeleted: 0 } }
  await commands.answerRequests([
    ...signedInMember,
    noPeople,
    batchesAre([accepted]),
    daysAre(twoDays, 5),
    entriesAre([coffee, walk, london]),
    { method: 'PUT', path: '/api/v1/import-batches/imp_1/entries/review', status: 409, body: problem(409, closed) },
  ])

  // Act
  await userEvent.click(screen.getByRole('button', { name: 'Reject the entry at 8:30 AM' }))

  // Assert
  await expect.element(screen.getByText(closed)).toBeVisible()
  await expect.element(screen.getByRole('list', { name: 'Result of the accept' })).toBeVisible()
})

test('@IMP-44 a 404 of an entry that the agent removed shows the message, and the page reads the entries again', async () => {
  // Arrange
  const { screen } = await renderReview()
  await commands.answerRequests([
    ...signedInMember,
    noPeople,
    batchesAre([diary]),
    daysAre([dayOf('2024-03-01', 1), dayOf('2024-03-02', 1)], 5),
    entriesAre([coffee, london]),
    { method: 'PUT', path: '/api/v1/import-batches/imp_1/entries/review', status: 404, body: problem(404, 'This entry is not in the batch.') },
  ])

  // Act
  await userEvent.click(screen.getByRole('button', { name: 'Reject the entry at 12:01 PM' }))

  // Assert
  await expect.element(screen.getByText('This entry is not in the batch.')).toBeVisible()
  await expect.element(cardOf(screen, '12:01 PM')).not.toBeInTheDocument()
  await expect.element(screen.getByLabelText('Counts of this batch')).toMatchTextContent('2 entries: 2 kept, 0 rejected')
  await expect.element(screen.getByRole('heading', { level: 1, name: 'Diary 2024' })).toHaveFocus()
})

test('@IMP-44 a 404 of a save in the form shows the message, though the form leaves with the entry', async () => {
  // Arrange
  const { screen } = await renderReview()
  await userEvent.click(screen.getByRole('button', { name: 'Edit the entry at 12:01 PM' }))
  const form = screen.getByRole('form', { name: 'Edit the entry' })
  await userEvent.click(form.getByRole('spinbutton', { name: /^hour,/ }))
  await userEvent.keyboard('0915')
  await commands.answerRequests([
    ...signedInMember,
    noPeople,
    batchesAre([diary]),
    daysAre([dayOf('2024-03-01', 1), dayOf('2024-03-02', 1)], 5),
    entriesAre([coffee, london]),
    { method: 'PUT', path: '/api/v1/import-batches/imp_1/entries/review', status: 404, body: problem(404, 'This entry is not in the batch.') },
  ])

  // Act
  await userEvent.click(form.getByRole('button', { name: 'Save' }))

  // Assert
  await expect.element(screen.getByText('This entry is not in the batch.')).toBeVisible()
  await expect.element(form).not.toBeInTheDocument()
  await expect.element(screen.getByRole('heading', { level: 1, name: 'Diary 2024' })).toHaveFocus()
})

test('@IMP-44 a batch that does not exist says so, with a link to the imports', async () => {
  // Arrange and Act
  const { screen } = await renderAt('/imports/imp_9', [batchesAre([diary])])

  // Assert
  await expect.element(screen.getByText(/^This import batch does not exist\. It may have been discarded\./)).toBeVisible()
  await expect.element(screen.getByRole('link', { name: 'Go to your imports' })).toHaveAttribute('href', '/imports')
})

test('@IMP-45 the setting says who can accept, and a change sends the version of the settings', async () => {
  // Arrange
  const screen = await renderSetting()
  const box = screen.getByRole('checkbox', { name: 'Only I can accept an import batch' })
  await expect.element(box).toBeChecked()
  await expect.element(box).toHaveAccessibleDescription(
    'With this on, only you can accept an import batch, in Recall in a browser. With it off, an agent with a personal access token can accept a batch too.',
  )
  const saved = { ...memberSetting, requireImportReview: false, version: 2 }
  await commands.answerRequests([...signedInMember, noPeople, { method: 'PUT', path: '/api/v1/me/settings/import-review', body: saved }])
  await commands.takeSentRequests()

  // Act
  await userEvent.click(screen.getByText('Only I can accept an import batch'))

  // Assert
  await expect.element(box).not.toBeChecked()
  expect(await commands.takeSentRequests()).toEqual([
    { method: 'PUT', path: '/api/v1/me/settings/import-review', body: { requireImportReview: false, version: 1 } },
  ])
})

test('@IMP-45 a change from an old version reads the settings again and sends the change once more', async () => {
  // Arrange
  const screen = await renderSetting()
  const box = screen.getByRole('checkbox', { name: 'Only I can accept an import batch' })
  await expect.element(box).toBeChecked()
  const stale = 'This record changed after you loaded it. Reload it, then make your change again.'
  await commands.answerRequests([
    ...signedInMember,
    noPeople,
    { method: 'GET', path: '/api/v1/me/settings', body: { ...memberSetting, version: 3 } },
    { method: 'PUT', path: '/api/v1/me/settings/import-review', status: 409, body: { ...problem(409, stale, 'urn:recall:problem:stale-version'), errors: { version: [stale] } } },
  ])
  await commands.takeSentRequests()

  // Act
  await userEvent.click(screen.getByText('Only I can accept an import batch'))

  // Assert
  await expect.element(screen.getByRole('alert').filter({ hasText: stale })).toBeVisible()
  expect(await commands.takeSentRequests()).toEqual([
    { method: 'PUT', path: '/api/v1/me/settings/import-review', body: { requireImportReview: false, version: 1 } },
    { method: 'PUT', path: '/api/v1/me/settings/import-review', body: { requireImportReview: false, version: 3 } },
  ])
})
