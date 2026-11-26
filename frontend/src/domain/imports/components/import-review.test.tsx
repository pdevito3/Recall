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
  const accepted: ImportBatchDto = {
    ...diary,
    id: 'imp_2',
    name: 'Diary 2023',
    state: 'Accepted',
    acceptedAt: '2026-11-21T10:00:00Z',
    entryCount: 0,
    result: { sessionsMade: 5, entriesRejected: 1, daysRejected: 0, skippedInJournal: 0, skippedDeleted: 0 },
  }

  const { screen } = await renderAt('/imports', [batchesAre([diary, accepted])])

  const open = screen.getByRole('listitem', { name: 'Diary 2024' })
  await expect.element(open).toMatchTextContent('Diary 2024Open3 entries. Times in Eastern Time.Opened on Nov 21, 2026, 12:00 AM.')
  const done = screen.getByRole('listitem', { name: 'Diary 2023' })
  await expect.element(done).toMatchTextContent('Diary 2023In your journal5 entries made in your journal. Times in Eastern Time.Opened on Nov 21, 2026, 12:00 AM. Accepted on Nov 22, 2026, 12:00 AM.')
  await expect.element(screen.getByRole('link', { name: 'Diary 2024' })).toHaveAttribute('href', '/imports/imp_1')
})

test('@IMP-33 with no batch, the list says that an agent opens one with a personal access token', async () => {
  const { screen } = await renderAt('/imports', [batchesAre([])])

  await expect.element(screen.getByText(/^You have no import batches\. An agent opens a batch with a personal access token, then you review it here\.$/)).toBeVisible()
  await expect.element(screen.getByRole('link', { name: 'personal access token' })).toHaveAttribute('href', '/settings/personal-access-tokens')
})

test('@IMP-34 discard asks with the name and the count, sends the delete, and the batch leaves the list', async () => {
  const { screen } = await renderAt('/imports', [batchesAre([diary])])
  await userEvent.click(screen.getByRole('button', { name: 'Discard Diary 2024' }))
  await expect.element(screen.getByRole('alertdialog', { name: 'Discard “Diary 2024”?' })).toMatchTextContent(
    'Discard deletes this batch and its 3 entries. None of them go into your journal. You cannot undo a discard.',
  )
  await commands.answerRequests([...signedInMember, noPeople, batchesAre([]), { method: 'DELETE', path: '/api/v1/import-batches/imp_1', status: 204 }])
  await commands.takeSentRequests()

  await userEvent.click(screen.getByRole('button', { name: 'Discard batch' }))

  await expect.element(screen.getByText('Batch discarded')).toBeVisible()
  await expect.element(screen.getByRole('listitem', { name: 'Diary 2024' })).not.toBeInTheDocument()
  expect(await commands.takeSentRequests()).toEqual([{ method: 'DELETE', path: '/api/v1/import-batches/imp_1', body: null }])
})

test('@IMP-34 an accepted batch has no Discard', async () => {
  const { screen } = await renderAt('/imports', [batchesAre([{ ...diary, state: 'Accepted' }])])

  await expect.element(screen.getByRole('listitem', { name: 'Diary 2024' })).toMatchTextContent('In your journal')
  await expect.element(screen.getByRole('button', { name: 'Discard Diary 2024' })).not.toBeInTheDocument()
})

test('@IMP-35 the review reads a page of whole days with from and to, and Next reads the next page', async () => {
  const days = [dayOf('2024-03-01', 150), dayOf('2024-03-02', 100)]
  const { screen, router } = await renderAt('/imports/imp_1', [
    batchesAre([diary]),
    daysAre(days),
    entriesAre([coffee], '2024-03-01', '2024-03-01'),
    entriesAre([london], '2024-03-02', '2024-03-02'),
  ])
  await expect.element(cardOf(screen, '8:30 AM')).toBeVisible()
  await expect.element(screen.getByRole('heading', { level: 2, name: 'Saturday, March 2, 2024' })).not.toBeInTheDocument()

  const pager = screen.getByRole('navigation', { name: 'Pages of this batch', exact: true })
  await expect.element(pager).toMatchTextContent('Page 1 of 2: Mar 1, 2024')

  await userEvent.click(pager.getByRole('button', { name: 'Next' }))

  await expect.element(cardOf(screen, '9:00 PM')).toBeVisible()
  await expect.element(screen.getByRole('heading', { level: 2, name: 'Friday, March 1, 2024' })).not.toBeInTheDocument()
  expect(router.state.location.search).toEqual({ page: 2 })
  await expect.element(pager).toMatchTextContent('Page 2 of 2: Mar 2, 2024')
  expect(await commands.takeUnansweredRequests()).toEqual([])
})

test('@IMP-35 the entries show in groups by day, in the order of the date and the position', async () => {
  const { screen } = await renderReview()

  const first = screen.getByRole('list', { name: 'Entries of Friday, March 1, 2024' })
  await expect.element(first.getByRole('listitem', { name: /^Entry at / }).first()).toHaveAccessibleName('Entry at 8:30 AM')
  await expect.element(first.getByRole('listitem', { name: /^Entry at / }).nth(1)).toHaveAccessibleName('Entry at 12:01 PM')
  await expect.element(screen.getByRole('list', { name: 'Entries of Saturday, March 2, 2024' }).getByRole('listitem', { name: /^Entry at / })).toHaveAccessibleName('Entry at 9:00 PM')
})

test('@IMP-35 a rejected day with no entry shows as a day', async () => {
  const { screen } = await renderAt('/imports/imp_1', [
    batchesAre([diary]),
    daysAre([dayOf('2024-03-01', 0, { rejected: true })]),
    entriesAre([], '2024-03-01', '2024-03-01'),
  ])

  await expect.element(screen.getByRole('heading', { level: 2, name: 'Friday, March 1, 2024' })).toBeVisible()
  await expect.element(screen.getByText('This day has no entries. While it is rejected, each entry that the import adds to it stays out.')).toBeVisible()
})

test('@IMP-36 each entry shows its start time in its zone, the zone when it is not the zone of the batch, its words, Moods, and Topics', async () => {
  const { screen } = await renderReview()

  await expect.element(cardOf(screen, '8:30 AM')).toMatchTextContent('8:30 AMCoffee on the porch.CalmMorning')
  await expect.element(cardOf(screen, '8:30 AM').getByRole('list', { name: 'Moods and topics' })).toMatchTextContent('CalmMorning')
  await expect.element(cardOf(screen, '9:00 PM')).toMatchTextContent('9:00 PMUnited Kingdom TimeFlew to London.')
})

test('@IMP-36 an entry with no time of the person or the agent says that its time comes from its place in the day', async () => {
  const { screen } = await renderReview()

  await expect.element(cardOf(screen, '12:01 PM')).toMatchTextContent('12:01 PMNo time given. Set from its place in the day.Long walk.')
  await expect.element(cardOf(screen, '8:30 AM')).not.toMatchTextContent('No time given')
})

test('@IMP-37 an entry whose words or time the import changed after the edit says that the accept keeps the edit', async () => {
  const flagged = { ...coffee, review: { rejected: false, words: documentOf('Mine.'), time: '09:00' }, wordsChangedAfterEdit: true, timeChangedAfterEdit: true }

  const { screen } = await renderAt('/imports/imp_1', [batchesAre([diary]), daysAre(twoDays), entriesAre([flagged, walk, london])])

  const card = cardOf(screen, '8:30 AM')
  await expect
    .element(card.getByText('The import changed the words after your edit. The accept keeps your edit. To use the new words, choose “Edit”, then “Use the import words”.'))
    .toBeVisible()
  await expect
    .element(card.getByText('The import changed the time after your edit. The accept keeps your time. To use the new time, choose “Edit”, then “Use the import time”.'))
    .toBeVisible()
  await expect.element(cardOf(screen, '12:01 PM').getByText(/The import changed/)).not.toBeInTheDocument()
})

test('@IMP-38 a save of a new time sends the whole review with the words of the last edit, and the entry shows the reply', async () => {
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

  await userEvent.click(form.getByRole('button', { name: 'Save' }))

  await expect.element(cardOf(screen, '9:15 AM')).toMatchTextContent('9:15 AMEdited')
  await expect.element(screen.getByRole('button', { name: 'Edit the entry at 9:15 AM' })).toHaveFocus()
  expect(await commands.takeSentRequests()).toEqual([
    { method: 'PUT', path: '/api/v1/import-batches/imp_1/entries/review', body: { clientSessionId: 'c1', rejected: false, words: null, time: '09:15' } },
  ])
})

test('@IMP-38 a save after a read that changed the agent time keeps the time of the agent, because the person did not change it', async () => {
  const { screen } = await renderReview()
  await userEvent.click(screen.getByRole('button', { name: 'Edit the entry at 8:30 AM' }))
  const form = screen.getByRole('form', { name: 'Edit the entry' })
  const moved = { ...coffee, time: '08:45', startsAt: '2024-03-01T13:45:00Z' }
  const rejected = { ...london, review: { rejected: true, words: null, time: null }, isRejected: true }
  await commands.answerRequests([
    ...signedInMember,
    noPeople,
    batchesAre([diary]),
    daysAre([dayOf('2024-03-01', 2), dayOf('2024-03-02', 1, { rejectedCount: 1 })], 5),
    entriesAre([moved, walk, rejected]),
    { method: 'PUT', path: '/api/v1/import-batches/imp_1/entries/review', body: { version: 5, entry: rejected } },
  ])
  await userEvent.click(screen.getByRole('button', { name: 'Reject the entry at 9:00 PM' }))
  await expect.element(screen.getByRole('listitem', { name: 'Entry at 8:45 AM' })).toBeVisible()
  await commands.answerRequests([
    ...signedInMember,
    noPeople,
    batchesAre([diary]),
    daysAre([dayOf('2024-03-01', 2), dayOf('2024-03-02', 1, { rejectedCount: 1 })], 6),
    entriesAre([moved, walk, rejected]),
    { method: 'PUT', path: '/api/v1/import-batches/imp_1/entries/review', body: { version: 6, entry: moved } },
  ])
  await commands.takeSentRequests()

  await userEvent.click(form.getByRole('button', { name: 'Save' }))

  await expect.element(form).not.toBeInTheDocument()
  expect(await commands.takeSentRequests()).toEqual([
    { method: 'PUT', path: '/api/v1/import-batches/imp_1/entries/review', body: { clientSessionId: 'c1', rejected: false, words: null, time: null } },
  ])
})

test('@IMP-38 Use the import words sends null for the words and keeps the time of the last edit', async () => {
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

  await userEvent.click(screen.getByRole('button', { name: 'Use the import words' }))

  await expect.element(screen.getByRole('form', { name: 'Edit the entry' })).not.toBeInTheDocument()
  expect(await commands.takeSentRequests()).toEqual([
    { method: 'PUT', path: '/api/v1/import-batches/imp_1/entries/review', body: { clientSessionId: 'c1', rejected: false, words: null, time: '09:00' } },
  ])
})

test('@IMP-38 a refused edit shows the message of the Server in the form, and the form keeps the change', async () => {
  const { screen } = await renderReview()
  await userEvent.click(screen.getByRole('button', { name: 'Edit the entry at 8:30 AM' }))
  const form = screen.getByRole('form', { name: 'Edit the entry' })
  await userEvent.click(form.getByRole('spinbutton', { name: /^hour,/ }))
  await userEvent.keyboard('1100')
  const refusal = { ...problem(422, 'The time of an entry cannot be in the future.'), errors: { time: ['The time of an entry cannot be in the future.'] } }
  await commands.answerRequests([...signedInMember, noPeople, batchesAre([diary]), daysAre(twoDays), entriesAre([coffee, walk, london]), { method: 'PUT', path: '/api/v1/import-batches/imp_1/entries/review', status: 422, body: refusal }])

  await userEvent.click(form.getByRole('button', { name: 'Save' }))

  await expect.element(form.getByRole('alert')).toMatchTextContent('The time of an entry cannot be in the future.')
  await expect.poll(() => form.getByRole('group', { name: 'Time' }).element().textContent).toMatch(/11\W*00\W*AM/)
  await expect.element(screen.getByText('The time of an entry cannot be in the future.', { exact: true }).first()).toBeVisible()
  expect(toastQueue.visibleToasts).toEqual([])
})

test('@IMP-38 a refusal of the words shows under Words, marks Words invalid, and moves the focus to it', async () => {
  const { screen } = await renderReview()
  await userEvent.click(screen.getByRole('button', { name: 'Edit the entry at 8:30 AM' }))
  const form = screen.getByRole('form', { name: 'Edit the entry' })
  const blank = 'The words of an entry cannot be empty.'
  await commands.answerRequests([
    ...signedInMember,
    noPeople,
    batchesAre([diary]),
    daysAre(twoDays),
    entriesAre([coffee, walk, london]),
    { method: 'PUT', path: '/api/v1/import-batches/imp_1/entries/review', status: 422, body: { ...problem(422, blank), errors: { words: [blank] } } },
  ])
  await form.getByRole('spinbutton', { name: /^hour,/ }).click()

  await userEvent.click(form.getByRole('button', { name: 'Save' }))

  const words = form.getByRole('textbox', { name: 'Words' })
  await expect.element(words).toHaveFocus()
  await expect.element(words).toHaveAttribute('aria-invalid', 'true')
  await expect.element(words).toHaveAccessibleDescription(blank)
  await expect.element(form.getByRole('alert')).toHaveTextContent(blank)
  const colorOf = (element: Element) => getComputedStyle(element).color
  expect(colorOf(form.getByText('Words', { exact: true }).element())).toBe(colorOf(form.getByRole('alert').element()))
})

test('@IMP-38 the time hint says what an empty time does: the time of the agent, else the place of the entry in its day', async () => {
  const { screen } = await renderReview()

  await userEvent.click(screen.getByRole('button', { name: 'Edit the entry at 8:30 AM' }))
  await expect.element(screen.getByRole('form', { name: 'Edit the entry' }).getByText('In Eastern Time. Leave it empty to use the import time.')).toBeVisible()
  await userEvent.click(screen.getByRole('button', { name: 'Cancel' }))
  await userEvent.click(screen.getByRole('button', { name: 'Edit the entry at 12:01 PM' }))
  await expect.element(screen.getByRole('form', { name: 'Edit the entry' }).getByText('In Eastern Time. Leave it empty to keep the place of the entry in its day.')).toBeVisible()
})

test('@IMP-38 Use the import words and Use the import time are off while the form has a change, and say why', async () => {
  const edited = { ...coffee, review: { rejected: false, words: documentOf('Mine.'), time: '09:00' }, document: documentOf('Mine.'), startsAt: '2024-03-01T14:00:00Z' }
  const { screen } = await renderAt('/imports/imp_1', [batchesAre([diary]), daysAre(twoDays), entriesAre([edited, walk, london])])
  await userEvent.click(screen.getByRole('button', { name: 'Edit the entry at 9:00 AM' }))
  const form = screen.getByRole('form', { name: 'Edit the entry' })
  await expect.element(form.getByRole('button', { name: 'Use the import words' })).toBeEnabled()

  await userEvent.click(form.getByRole('spinbutton', { name: /^hour,/ }))
  await userEvent.keyboard('10')

  for (const name of ['Use the import words', 'Use the import time']) {
    await expect.element(form.getByRole('button', { name })).toBeDisabled()
    await expect.element(form.getByRole('button', { name })).toHaveAccessibleDescription('Save or cancel your change first.')
  }
  await expect.element(form.getByText('Save or cancel your change first.')).toBeVisible()
})

test('@IMP-39 Reject sends the whole review with rejected, and the entry then says Rejected with Keep', async () => {
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

  await userEvent.click(screen.getByRole('button', { name: 'Reject the entry at 12:01 PM' }))

  await expect.element(cardOf(screen, '12:01 PM')).toMatchTextContent('Rejected')
  await expect.element(screen.getByRole('button', { name: 'Keep the entry at 12:01 PM' })).toBeVisible()
  expect(await commands.takeSentRequests()).toEqual([
    { method: 'PUT', path: '/api/v1/import-batches/imp_1/entries/review', body: { clientSessionId: 'c2', rejected: true, words: null, time: null } },
  ])
})

test('@IMP-40 Reject day sends the review of the day, the heading says the day is rejected, and each entry says rejected with the day, with no Keep', async () => {
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

  await userEvent.click(screen.getByRole('button', { name: 'Reject Friday, March 1, 2024' }))

  await expect.element(cardOf(screen, '8:30 AM')).toMatchTextContent('Rejected with the day')
  await expect.element(cardOf(screen, '12:01 PM')).toMatchTextContent('Rejected with the day')
  await expect.element(screen.getByRole('region', { name: 'Friday, March 1, 2024' })).toMatchTextContent('Friday, March 1, 20242 entries, day rejectedKeep day')
  await expect.element(cardOf(screen, '8:30 AM').getByRole('button', { name: /^(Keep|Reject) the entry/ })).not.toBeInTheDocument()
  await expect.element(screen.getByRole('button', { name: 'Keep Friday, March 1, 2024' })).toBeVisible()
  expect(await commands.takeSentRequests()).toEqual([{ method: 'PUT', path: '/api/v1/import-batches/imp_1/days/review', body: { date: '2024-03-01', rejected: true } }])
})

test('@IMP-40 an entry that the person rejected alone stays rejected, with Keep, when its day is kept again', async () => {
  const alone = { ...walk, review: { rejected: true, words: null, time: null }, isRejected: true }

  const { screen } = await renderAt('/imports/imp_1', [batchesAre([diary]), daysAre([dayOf('2024-03-01', 2, { rejectedCount: 1 }), dayOf('2024-03-02', 1)]), entriesAre([coffee, alone, london])])

  await expect.element(cardOf(screen, '12:01 PM')).toMatchTextContent('Rejected')
  await expect.element(cardOf(screen, '12:01 PM')).not.toMatchTextContent('with the day')
  await expect.element(screen.getByRole('button', { name: 'Keep the entry at 12:01 PM' })).toBeVisible()
})

test('@IMP-40 on a rejected day, an entry that the person rejected alone says Rejected, not rejected with the day, so it stays rejected when the day is kept', async () => {
  const alone = { ...walk, review: { rejected: true, words: null, time: null }, isRejected: true }
  const held = { ...coffee, isRejected: true }

  const { screen } = await renderAt('/imports/imp_1', [
    batchesAre([diary]),
    daysAre([dayOf('2024-03-01', 2, { rejected: true, rejectedCount: 1 }), dayOf('2024-03-02', 1)]),
    entriesAre([held, alone, london]),
  ])

  await expect.element(cardOf(screen, '12:01 PM')).toMatchTextContent('Rejected')
  await expect.element(cardOf(screen, '12:01 PM')).not.toMatchTextContent('with the day')
  await expect.element(cardOf(screen, '8:30 AM')).toMatchTextContent('Rejected with the day')
  await expect.element(cardOf(screen, '12:01 PM').getByRole('button', { name: /^(Keep|Reject) the entry/ })).not.toBeInTheDocument()
})

test('@IMP-40 @IMP-44 Keep day on a rejected day with no entries takes the day off the page and moves the focus to the heading of the batch', async () => {
  const { screen } = await renderAt('/imports/imp_1', [
    batchesAre([diary]),
    daysAre([...twoDays, dayOf('2024-03-03', 0, { rejected: true })]),
    entriesAre([coffee, walk, london], '2024-03-01', '2024-03-03'),
  ])
  await expect.element(screen.getByRole('button', { name: 'Keep Sunday, March 3, 2024' })).toBeVisible()
  await commands.answerRequests([
    ...signedInMember,
    noPeople,
    batchesAre([diary]),
    daysAre(twoDays, 5),
    entriesAre([coffee, walk, london]),
    entriesAre([coffee, walk, london], '2024-03-01', '2024-03-03'),
    { method: 'PUT', path: '/api/v1/import-batches/imp_1/days/review', body: { version: 5, date: '2024-03-03', rejected: false } },
  ])

  await userEvent.click(screen.getByRole('button', { name: 'Keep Sunday, March 3, 2024' }))

  await expect.element(screen.getByRole('region', { name: 'Sunday, March 3, 2024' })).not.toBeInTheDocument()
  await expect.element(screen.getByRole('heading', { level: 1, name: 'Diary 2024' })).toHaveFocus()
})

test('@IMP-41 the counts come from the days, and a review reads the days again', async () => {
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

  await userEvent.click(screen.getByRole('button', { name: 'Reject the entry at 12:01 PM' }))

  await expect.element(screen.getByLabelText('Counts of this batch')).toMatchTextContent('3 entries: 1 kept, 2 rejected')
})

test('@IMP-42 Accept asks with the kept and rejected counts, and sends the version of the last read of the days', async () => {
  const { screen } = await renderAt('/imports/imp_1', [
    batchesAre([diary]),
    daysAre([dayOf('2024-03-01', 2, { rejectedCount: 1 }), dayOf('2024-03-02', 1)], 9),
    entriesAre([coffee, walk, london]),
  ])
  await userEvent.click(screen.getByRole('button', { name: 'Accept', exact: true }))
  const dialog = screen.getByRole('alertdialog', { name: 'Accept “Diary 2024”?' })
  await expect.element(dialog).toMatchTextContent('2 entries go into your journal, and 1 entry that you rejected stays out.')
  const accepted: ImportBatchDto = {
    ...diary,
    state: 'Accepted',
    acceptedAt: '2026-11-25T10:00:00Z',
    result: { sessionsMade: 2, entriesRejected: 1, daysRejected: 0, skippedInJournal: 0, skippedDeleted: 0 },
  }
  await commands.answerRequests([...signedInMember, noPeople, batchesAre([accepted]), { method: 'POST', path: '/api/v1/import-batches/imp_1/accept', body: accepted }])
  await commands.takeSentRequests()

  await userEvent.click(dialog.getByRole('button', { name: 'Accept batch' }))

  await expect.element(screen.getByRole('list', { name: 'Result of the accept' })).toBeVisible()
  expect(await commands.takeSentRequests()).toEqual([{ method: 'POST', path: '/api/v1/import-batches/imp_1/accept', body: { version: 9 } }])
})

test('@IMP-42 with no kept entry, Accept is off and says to keep an entry or to discard the batch', async () => {
  const { screen } = await renderAt('/imports/imp_1', [
    batchesAre([diary]),
    daysAre([dayOf('2024-03-01', 2, { rejected: true }), dayOf('2024-03-02', 1, { rejectedCount: 1 })]),
    entriesAre([coffee, walk, london]),
  ])

  const accept = screen.getByRole('button', { name: 'Accept', exact: true })
  await expect.element(accept).toBeDisabled()
  await expect.element(accept).toHaveAccessibleDescription('Keep an entry to accept this batch, or discard it.')
})

test('@IMP-42 with no entry, Accept is off and says to wait for the agent or to discard the batch', async () => {
  const { screen } = await renderAt('/imports/imp_1', [batchesAre([{ ...diary, entryCount: 0 }]), daysAre([])])

  const accept = screen.getByRole('button', { name: 'Accept', exact: true })
  await expect.element(accept).toBeDisabled()
  await expect.element(accept).toHaveAccessibleDescription('Wait for the agent to add entries, or discard this batch.')
})

test('@IMP-42 Accept is off while a review and the read of the days after it are not done', async () => {
  const { screen } = await renderReview()
  const accept = screen.getByRole('button', { name: 'Accept', exact: true })
  await expect.element(accept).toBeEnabled()
  const rejected = { ...walk, review: { rejected: true, words: null, time: null }, isRejected: true }
  await commands.answerRequests([
    ...signedInMember,
    noPeople,
    batchesAre([diary]),
    daysAre([dayOf('2024-03-01', 2, { rejectedCount: 1 }), dayOf('2024-03-02', 1)], 5),
    entriesAre([coffee, rejected, london]),
    { method: 'PUT', path: '/api/v1/import-batches/imp_1/entries/review', body: { version: 5, entry: rejected } },
  ])
  await commands.holdRequests(daysPath)

  await userEvent.click(screen.getByRole('button', { name: 'Reject the entry at 12:01 PM' }))

  await expect.element(accept).toBeDisabled()
  await commands.releaseRequests(daysPath)
  await expect.element(screen.getByLabelText('Counts of this batch')).toMatchTextContent('3 entries: 2 kept, 1 rejected')
  await expect.element(accept).toBeEnabled()
})

test('@IMP-42 Accept is off after a refused review until the read of the days after it is done', async () => {
  const { screen } = await renderReview()
  const accept = screen.getByRole('button', { name: 'Accept', exact: true })
  await expect.element(accept).toBeEnabled()
  await commands.answerRequests([
    ...signedInMember,
    noPeople,
    batchesAre([diary]),
    daysAre([dayOf('2024-03-01', 1), dayOf('2024-03-02', 1)], 5),
    entriesAre([coffee, london]),
    { method: 'PUT', path: '/api/v1/import-batches/imp_1/entries/review', status: 404, body: problem(404, 'This entry is no longer in the batch. Review the other entries of the batch.') },
  ])
  await commands.holdRequests(daysPath)

  await userEvent.click(screen.getByRole('button', { name: 'Reject the entry at 12:01 PM' }))

  await expect.poll(() => commands.countHeldRequests(daysPath)).toBe(1)
  await expect.element(accept).toBeDisabled()
  await commands.releaseRequests(daysPath)
  await expect.element(screen.getByLabelText('Counts of this batch')).toMatchTextContent('2 entries: 2 kept, 0 rejected')
  await expect.element(accept).toBeEnabled()
})

test('@IMP-44 a 404 of the accept reads the batches again, and the page says that the batch does not exist', async () => {
  const { screen } = await renderAt('/imports/imp_1', [batchesAre([diary]), daysAre([dayOf('2024-03-01', 1)]), entriesAre([coffee], '2024-03-01', '2024-03-01')])
  await userEvent.click(screen.getByRole('button', { name: 'Accept', exact: true }))
  const dialog = screen.getByRole('alertdialog', { name: 'Accept “Diary 2024”?' })
  await expect.element(dialog).toMatchTextContent('1 entry goes into your journal, and 0 entries that you rejected stay out.')
  const gone = 'This import batch does not exist.'
  await commands.answerRequests([
    ...signedInMember,
    noPeople,
    batchesAre([]),
    { method: 'GET', path: daysPath, status: 404, body: problem(404, gone) },
    { method: 'GET', path: entriesPath, status: 404, body: problem(404, gone) },
    { method: 'POST', path: '/api/v1/import-batches/imp_1/accept', status: 404, body: problem(404, gone) },
  ])

  await userEvent.click(dialog.getByRole('button', { name: 'Accept batch' }))

  await expect.element(screen.getByRole('heading', { level: 1, name: 'Batch not found' })).toBeVisible()
})

test('@IMP-34 Discard on the review names the count of the days that the review read', async () => {
  const { screen } = await renderAt('/imports/imp_1', [batchesAre([diary]), daysAre([...twoDays, dayOf('2024-03-03', 1)]), entriesAre([coffee, walk, london], '2024-03-01', '2024-03-03')])
  await expect.element(screen.getByLabelText('Counts of this batch')).toMatchTextContent('4 entries')

  await userEvent.click(screen.getByRole('button', { name: 'Discard Diary 2024' }))

  await expect.element(screen.getByRole('alertdialog', { name: 'Discard “Diary 2024”?' })).toMatchTextContent('Discard deletes this batch and its 4 entries.')
})

test('@IMP-43 an accepted batch shows the counts of its result and a link to the journal, and no review action', async () => {
  const accepted: ImportBatchDto = {
    ...diary,
    state: 'Accepted',
    acceptedAt: '2026-11-25T10:00:00Z',
    result: { sessionsMade: 12, entriesRejected: 3, daysRejected: 1, skippedInJournal: 2, skippedDeleted: 1 },
  }

  const { screen } = await renderAt('/imports/imp_1', [batchesAre([accepted])])

  await expect.element(screen.getByText('This batch is in your journal. Accepted on Nov 26, 2026, 12:00 AM.')).toBeVisible()
  const lines = screen.getByRole('list', { name: 'Result of the accept' }).getByRole('listitem')
  expect(lines.elements().map((line) => line.textContent)).toEqual([
    '12 entries made in your journal',
    '3 entries left out because you rejected them or their day',
    '1 day that you rejected',
    '2 entries skipped because your journal has them',
    '1 entry skipped because you deleted it from your journal',
  ])
  await expect.element(screen.getByRole('link', { name: 'Go to your journal' })).toHaveAttribute('href', '/')
  await expect.element(screen.getByRole('button', { name: 'Accept', exact: true })).not.toBeInTheDocument()
  await expect.element(screen.getByRole('heading', { level: 1, name: 'Diary 2024' })).toHaveFocus()
})

test('@IMP-44 a 409 of a changed batch keeps the dialog open, and after a new read shows its counts and one status line above them, in the color of the text, not the message of the Server', async () => {
  const { screen } = await renderReview()
  await userEvent.click(screen.getByRole('button', { name: 'Accept', exact: true }))
  const dialog = screen.getByRole('alertdialog', { name: 'Accept “Diary 2024”?' })
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
  await commands.holdRequests(daysPath)

  await userEvent.click(dialog.getByRole('button', { name: 'Accept batch' }))

  await expect.poll(() => commands.countHeldRequests(daysPath)).toBe(1)
  await expect.element(dialog.getByRole('button', { name: 'Accept batch' })).toHaveAttribute('aria-disabled', 'true')
  await expect.element(dialog.getByRole('alert')).not.toBeInTheDocument()
  await expect.element(dialog.getByRole('status')).not.toBeInTheDocument()
  await expect.element(dialog).toMatchTextContent('3 entries go into your journal')
  await commands.releaseRequests(daysPath)
  await expect.element(dialog.getByRole('status')).toHaveTextContent('The batch changed. Recall read it again. Check the new counts, then accept the batch.')
  const status = dialog.getByRole('status').element()
  const counts = dialog.getByText(/entries go into your journal/).element()
  expect(status.compareDocumentPosition(counts) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
  expect(getComputedStyle(status).color).toBe(getComputedStyle(dialog.element()).color)
  await expect.element(dialog.getByRole('alert')).not.toBeInTheDocument()
  await expect.element(dialog).toMatchTextContent('4 entries go into your journal')
  await expect.element(dialog.getByRole('button', { name: 'Accept batch' })).not.toHaveAttribute('aria-disabled')
  await commands.answerRequests([
    ...signedInMember,
    noPeople,
    batchesAre([diary]),
    daysAre([...twoDays, dayOf('2024-03-03', 1)], 7),
    entriesAre([coffee, walk, london], '2024-03-01', '2024-03-03'),
    { method: 'POST', path: '/api/v1/import-batches/imp_1/accept', status: 409, body: problem(409, changed, 'urn:recall:problem:stale-version') }])
  await userEvent.click(dialog.getByRole('button', { name: 'Accept batch' }))
  await expect.element(dialog.getByRole('status')).toHaveTextContent('The batch changed. Recall read it again. Check the new counts, then accept the batch.')
  await expect.element(dialog.getByRole('alert')).not.toBeInTheDocument()
  const sent: unknown[] = []
  await expect.poll(async () => sent.push(...(await commands.takeSentRequests()).map((r) => r.body))).toBe(2)
  expect(sent).toEqual([{ version: 4 }, { version: 7 }])
})

test('@IMP-44 a 409 of a review of a batch that an accept closed shows the message, and the page reads the batch again', async () => {
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

  await userEvent.click(screen.getByRole('button', { name: 'Reject the entry at 8:30 AM' }))

  await expect.element(screen.getByText(closed)).toBeVisible()
  await expect.element(screen.getByRole('list', { name: 'Result of the accept' })).toBeVisible()
})

test('@IMP-44 a 404 of an entry that the agent removed shows the message, and the page reads the entries again', async () => {
  const { screen } = await renderReview()
  await commands.answerRequests([
    ...signedInMember,
    noPeople,
    batchesAre([diary]),
    daysAre([dayOf('2024-03-01', 1), dayOf('2024-03-02', 1)], 5),
    entriesAre([coffee, london]),
    { method: 'PUT', path: '/api/v1/import-batches/imp_1/entries/review', status: 404, body: problem(404, 'This entry is no longer in the batch. Review the other entries of the batch.') },
  ])

  await userEvent.click(screen.getByRole('button', { name: 'Reject the entry at 12:01 PM' }))

  await expect.element(screen.getByText('This entry is no longer in the batch. Review the other entries of the batch.')).toBeVisible()
  await expect.element(cardOf(screen, '12:01 PM')).not.toBeInTheDocument()
  await expect.element(screen.getByLabelText('Counts of this batch')).toMatchTextContent('2 entries: 2 kept, 0 rejected')
  await expect.element(screen.getByRole('heading', { level: 1, name: 'Diary 2024' })).toHaveFocus()
})

test('@IMP-44 a 404 of a save in the form shows the message, though the form leaves with the entry', async () => {
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
    { method: 'PUT', path: '/api/v1/import-batches/imp_1/entries/review', status: 404, body: problem(404, 'This entry is no longer in the batch. Review the other entries of the batch.') },
  ])

  await userEvent.click(form.getByRole('button', { name: 'Save' }))

  await expect.element(screen.getByText('This entry is no longer in the batch. Review the other entries of the batch.')).toBeVisible()
  await expect.element(form).not.toBeInTheDocument()
  await expect.element(screen.getByRole('heading', { level: 1, name: 'Diary 2024' })).toHaveFocus()
})

test('@IMP-44 a batch that does not exist says so, with a link to the imports', async () => {
  const { screen } = await renderAt('/imports/imp_9', [batchesAre([diary])])

  await expect.element(screen.getByRole('heading', { level: 1, name: 'Batch not found' })).toBeVisible()
  await expect.element(screen.getByText('This import batch does not exist. Pick a batch from your imports.', { exact: true })).toBeVisible()
  await expect.element(screen.getByRole('link', { name: 'Go to your imports' })).toHaveAttribute('href', '/imports')
})

test('@IMP-33 @IMP-34 @IMP-42 @IMP-43 a count of 1 takes the word for one in the list, the discard, the accept, and the result', async () => {
  const one: ImportBatchDto = { ...diary, entryCount: 1 }
  const done: ImportBatchDto = {
    ...diary,
    id: 'imp_2',
    name: 'Diary 2023',
    state: 'Accepted',
    acceptedAt: '2026-11-21T10:00:00Z',
    entryCount: 0,
    result: { sessionsMade: 1, entriesRejected: 1, daysRejected: 1, skippedInJournal: 1, skippedDeleted: 1 },
  }
  const { screen, router } = await renderAt('/imports', [
    batchesAre([one, done]),
    daysAre([dayOf('2024-03-01', 1), dayOf('2024-03-02', 1, { rejectedCount: 1 })]),
    entriesAre([coffee, london]),
  ])

  await expect.element(screen.getByRole('listitem', { name: 'Diary 2024' })).toMatchTextContent('1 entry. Times in Eastern Time.')
  await expect.element(screen.getByRole('listitem', { name: 'Diary 2023' })).toMatchTextContent('1 entry made in your journal.')
  await userEvent.click(screen.getByRole('button', { name: 'Discard Diary 2024' }))
  await expect
    .element(screen.getByRole('alertdialog', { name: 'Discard “Diary 2024”?' }))
    .toMatchTextContent('Discard deletes this batch and its 1 entry. The entry does not go into your journal. You cannot undo a discard.')
  await userEvent.click(screen.getByRole('button', { name: 'Keep batch' }))

  await router.navigate({ to: '/imports/$id', params: { id: 'imp_1' } })
  await userEvent.click(screen.getByRole('button', { name: 'Accept', exact: true }))
  await expect
    .element(screen.getByRole('alertdialog', { name: 'Accept “Diary 2024”?' }))
    .toMatchTextContent('1 entry goes into your journal, and 1 entry that you rejected stays out.')
  await userEvent.click(screen.getByRole('button', { name: 'Keep reviewing' }))

  await router.navigate({ to: '/imports/$id', params: { id: 'imp_2' } })
  const lines = screen.getByRole('list', { name: 'Result of the accept' }).getByRole('listitem')
  await expect.element(lines.first()).toBeVisible()
  expect(lines.elements().map((line) => line.textContent)).toEqual([
    '1 entry made in your journal',
    '1 entry left out because you rejected it or its day',
    '1 day that you rejected',
    '1 entry skipped because your journal has it',
    '1 entry skipped because you deleted it from your journal',
  ])
})

test('@IMP-43 a row of the result with a count of 0 is muted', async () => {
  const accepted: ImportBatchDto = { ...diary, state: 'Accepted', acceptedAt: '2026-11-25T10:00:00Z', result: { sessionsMade: 2, entriesRejected: 0, daysRejected: 0, skippedInJournal: 0, skippedDeleted: 0 } }

  const { screen } = await renderAt('/imports/imp_1', [batchesAre([accepted])])

  const list = screen.getByRole('list', { name: 'Result of the accept' })
  const colorOf = (text: string) => getComputedStyle(list.getByText(text).element()).color
  await expect.element(list.getByText('2 entries made in your journal')).toBeVisible()
  expect(colorOf('0 days that you rejected')).not.toBe(colorOf('2 entries made in your journal'))
  expect(colorOf('0 days that you rejected')).toBe(colorOf('0 entries skipped because your journal has them'))
})

test('@IMP-45 the setting says who can accept, and a change sends the version of the settings', async () => {
  const screen = await renderSetting()
  const box = screen.getByRole('checkbox', { name: 'Only I can accept an import batch' })
  await expect.element(box).toBeChecked()
  await expect.element(box).toHaveAccessibleDescription(
    'With this on, only you can accept an import batch, and only in Recall in a browser. With it off, an app or an agent that can write to your journal, such as an agent with a personal access token, can accept a batch too.',
  )
  const saved = { ...memberSetting, requireImportReview: false, version: 2 }
  await commands.answerRequests([...signedInMember, noPeople, { method: 'PUT', path: '/api/v1/me/settings/import-review', body: saved }])
  await commands.takeSentRequests()

  await userEvent.click(screen.getByText('Only I can accept an import batch'))

  await expect.element(box).not.toBeChecked()
  expect(await commands.takeSentRequests()).toEqual([
    { method: 'PUT', path: '/api/v1/me/settings/import-review', body: { requireImportReview: false, version: 1 } },
  ])
})

test('@IMP-45 a change from an old version reads the settings again and sends the change once more', async () => {
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

  await userEvent.click(screen.getByText('Only I can accept an import batch'))

  await expect.element(screen.getByRole('alert').filter({ hasText: stale })).toBeVisible()
  expect(await commands.takeSentRequests()).toEqual([
    { method: 'PUT', path: '/api/v1/me/settings/import-review', body: { requireImportReview: false, version: 1 } },
    { method: 'PUT', path: '/api/v1/me/settings/import-review', body: { requireImportReview: false, version: 3 } },
  ])
})
