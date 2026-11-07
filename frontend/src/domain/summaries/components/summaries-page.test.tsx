import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { createMemoryHistory, createRouter, RouterProvider } from '@tanstack/react-router'
import { afterEach, beforeEach, expect, test } from 'vitest'
import { commands, page, userEvent } from 'vitest/browser'
import { render } from 'vitest-browser-react'

import type { SummaryDto } from '@/api/generated/models/types/summary-dto'
import { apiClient } from '@/lib/api-client'
import { browserTimeZone, todayAt } from '@/lib/zoned-time'
import { routeTree } from '@/routeTree.gen'
import type { Answer } from '@/test/answer'
import { signedInMember } from '@/test/session'

import { pollWhileGeneratingMs } from '../apis/summaries'

const week = (change: Partial<SummaryDto> = {}): SummaryDto => ({
  id: 'sum_1',
  period: 'Week',
  periodDate: '2026-06-01',
  periodLastDate: '2026-06-07',
  status: 'Ready',
  failure: 'None',
  content: 'The first words of the week.\n\nThe second paragraph.',
  sourceCount: 3,
  generatedAt: '2026-06-07T20:00:00Z',
  ...change,
})

const read = (path: string, body: SummaryDto): Answer => ({ method: 'GET', path, body })
const open = (path: string, body: SummaryDto): Answer => ({ method: 'POST', path: `${path}/generate`, body })

const weekPath = '/api/v1/summaries/Week/2026-06-03'
const sent: string[] = []
let recorder = 0

beforeEach(() => {
  sent.length = 0
  recorder = apiClient.interceptors.request.use((config) => {
    sent.push(`${config.method?.toUpperCase()} ${config.url}`)
    return config
  })
})

afterEach(() => {
  apiClient.interceptors.request.eject(recorder)
})

const sentCount = (request: string) => sent.filter((each) => each === request).length

async function openPage(path: string, answers: Answer[]) {
  await commands.answerRequests([...signedInMember, ...answers])
  const router = createRouter({ routeTree, history: createMemoryHistory({ initialEntries: [path] }) })
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const screen = await render(
    <QueryClientProvider client={client}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  )
  return { screen, router }
}

test('opens the period once, reads it again while it is written, and stops when it is ready', async () => {
  // Arrange
  const writing = week({ status: 'Generating', content: '', generatedAt: null })
  const { screen } = await openPage('/summaries?period=Week&date=2026-06-03', [open(weekPath, writing), read(weekPath, writing)])
  await expect.element(screen.getByText('Writing the Summary. It can take a minute.')).toBeVisible()
  await expect.poll(() => sentCount(`GET ${weekPath}`), { timeout: pollWhileGeneratingMs * 3 }).toBeGreaterThan(0)

  // Act
  await commands.answerRequests([...signedInMember, open(weekPath, writing), read(weekPath, week())])

  // Assert
  await expect.element(screen.getByText('The first words of the week.'), { timeout: pollWhileGeneratingMs * 3 }).toBeVisible()
  await expect.element(screen.getByText('The second paragraph.')).toBeVisible()
  await expect.element(screen.getByText('Up to date.')).toBeVisible()
  await expect.element(screen.getByText(/^Reads 3 Sessions · Written /)).toBeVisible()
  const readsWhenReady = sentCount(`GET ${weekPath}`)
  await new Promise((resolve) => setTimeout(resolve, pollWhileGeneratingMs * 1.5))
  expect(sentCount(`GET ${weekPath}`)).toBe(readsWhenReady)
  expect(sentCount(`POST ${weekPath}/generate`)).toBe(1)
})

test('a failed Summary shows why in an alert with Retry, and never its words or the ready status', async () => {
  // Arrange
  const failed = week({ status: 'Failed', failure: 'ProviderUnavailable', content: 'Words that must not show.' })
  const { screen } = await openPage('/summaries?period=Week&date=2026-06-03', [open(weekPath, failed)])

  // Assert
  await expect.element(screen.getByRole('alert').getByText('The model provider did not answer. Try again later.')).toBeVisible()
  await expect.element(screen.getByRole('button', { name: 'Retry' })).toBeVisible()
  await expect.element(screen.getByText('This Summary was not written.')).toBeVisible()
  await expect.element(screen.getByText('Words that must not show.')).not.toBeInTheDocument()
  await expect.element(screen.getByText('Up to date.')).not.toBeInTheDocument()
  await expect.element(screen.getByText(/ProviderUnavailable/)).not.toBeInTheDocument()
})

test('Retry opens the period again and shows that the Summary is written', async () => {
  // Arrange
  const failed = week({ status: 'Failed', failure: 'Interrupted', content: '' })
  const writing = week({ status: 'Generating', content: '', generatedAt: null })
  const { screen } = await openPage('/summaries?period=Week&date=2026-06-03', [open(weekPath, failed)])
  await expect.element(screen.getByRole('button', { name: 'Retry' })).toBeVisible()
  await commands.answerRequests([...signedInMember, open(weekPath, writing), read(weekPath, writing)])

  // Act
  await userEvent.click(screen.getByRole('button', { name: 'Retry' }))

  // Assert
  await expect.element(screen.getByText('Writing the Summary. It can take a minute.')).toBeVisible()
  await expect.element(screen.getByRole('alert')).not.toBeInTheDocument()
  await expect.element(screen.getByRole('heading', { level: 2, name: 'Mon, Jun 1 - Sun, Jun 7, 2026' })).toHaveFocus()
  expect(sentCount(`POST ${weekPath}/generate`)).toBe(2)
})

test('with no model provider, the alert says that an Admin must connect one', async () => {
  // Arrange
  const { screen } = await openPage('/summaries?period=Week&date=2026-06-03', [open(weekPath, week({ status: 'Failed', failure: 'NoModelProvider', content: '' }))])

  // Assert
  await expect.element(screen.getByRole('alert').getByText('An Admin must connect a model provider, pick a model, and make it active first.')).toBeVisible()
})

test('a period with no Sessions is calm: no Summary, no button, and no second read', async () => {
  // Arrange
  const dayPath = '/api/v1/summaries/Day/2026-06-03'
  const empty = week({ id: null, period: 'Day', periodDate: '2026-06-03', periodLastDate: '2026-06-03', status: 'Missing', content: '', sourceCount: 0, generatedAt: null })
  const { screen } = await openPage('/summaries?date=2026-06-03', [open(dayPath, empty)])

  // Assert
  await expect.element(screen.getByText('No Sessions in this period.')).toBeVisible()
  await expect.element(screen.getByRole('heading', { level: 2, name: 'Wed, Jun 3, 2026' })).toBeVisible()
  await expect.element(screen.getByRole('alert')).not.toBeInTheDocument()
  await expect.element(screen.getByText(/^Reads/)).not.toBeInTheDocument()
  await expect.element(screen.getByRole('button', { name: /Generate|Retry|Regenerate/ })).not.toBeInTheDocument()
  expect(sentCount(`GET ${dayPath}`)).toBe(0)
})

test('a stale Summary keeps its words, says that it is out of date, and Regenerate opens it again', async () => {
  // Arrange
  const stale = week({ status: 'Stale', content: 'Old words.' })
  const writing = week({ status: 'Generating', content: '', generatedAt: null })
  const { screen } = await openPage('/summaries?period=Week&date=2026-06-03', [open(weekPath, stale)])
  await expect.element(screen.getByText('Old words.')).toBeVisible()
  await expect.element(screen.getByText('Out of date. Something in this period changed after the Summary was written.')).toBeVisible()
  await commands.answerRequests([...signedInMember, open(weekPath, writing), read(weekPath, writing)])

  // Act
  await userEvent.click(screen.getByRole('button', { name: 'Regenerate' }))

  // Assert
  await expect.element(screen.getByText('Writing the Summary. It can take a minute.')).toBeVisible()
  await expect.element(screen.getByText('Old words.')).not.toBeInTheDocument()
})

test('Next week opens the week after, and a tab opens the period of the same date', async () => {
  // Arrange
  const nextWeekPath = '/api/v1/summaries/Week/2026-06-10'
  const monthPath = '/api/v1/summaries/Month/2026-06-10'
  const nextWeek = week({ periodDate: '2026-06-08', periodLastDate: '2026-06-14', content: 'The next week.' })
  const june = week({ period: 'Month', periodDate: '2026-06-01', periodLastDate: '2026-06-30', content: 'All of June.', sourceCount: 2 })
  const { screen, router } = await openPage('/summaries?period=Week&date=2026-06-03', [
    open(weekPath, week()),
    open(nextWeekPath, nextWeek),
    open(monthPath, june),
  ])
  await expect.element(screen.getByText('The first words of the week.')).toBeVisible()

  // Act
  await userEvent.click(screen.getByRole('link', { name: 'Next week' }))

  // Assert
  await expect.element(screen.getByText('The next week.')).toBeVisible()
  await expect.element(screen.getByRole('heading', { level: 2, name: 'Mon, Jun 8 - Sun, Jun 14, 2026' })).toBeVisible()

  // Act
  await userEvent.click(screen.getByRole('tab', { name: 'Month' }))

  // Assert
  await expect.element(screen.getByText('All of June.')).toBeVisible()
  await expect.element(screen.getByText(/^Reads 2 days · Written /)).toBeVisible()
  await expect.element(screen.getByRole('tab', { name: 'Month' })).toHaveAttribute('aria-selected', 'true')
  expect(router.state.location.search).toEqual({ period: 'Month', date: '2026-06-10' })
})

test('a second press of Next while the next week loads goes on to the week after it', async () => {
  // Arrange
  const third = week({ periodDate: '2026-06-15', periodLastDate: '2026-06-21', content: 'The third week.' })
  const { screen, router } = await openPage('/summaries?period=Week&date=2026-06-03', [
    open(weekPath, week()),
    open('/api/v1/summaries/Week/2026-06-10', week({ periodDate: '2026-06-08', periodLastDate: '2026-06-14', content: 'The next week.' })),
    open('/api/v1/summaries/Week/2026-06-17', third),
  ])
  await expect.element(screen.getByText('The first words of the week.')).toBeVisible()
  const next = screen.getByRole('link', { name: 'Next week' })

  // Act
  await userEvent.click(next)
  await expect.poll(() => router.state.location.search).toEqual({ period: 'Week', date: '2026-06-10' })
  await userEvent.click(next)

  // Assert
  await expect.element(screen.getByText('The third week.')).toBeVisible()
  expect(router.state.location.search).toEqual({ period: 'Week', date: '2026-06-17' })
})

test('Previous is off in the first year that the Server takes', async () => {
  // Arrange
  const path = '/api/v1/summaries/Year/1900-06-01'
  const first = week({ period: 'Year', periodDate: '1900-01-01', periodLastDate: '1900-12-31', content: 'The first year.' })
  const { screen } = await openPage('/summaries?period=Year&date=1900-06-01', [open(path, first)])

  // Assert
  await expect.element(screen.getByText('The first year.')).toBeVisible()
  await expect.element(screen.getByRole('link', { name: 'Previous year' })).toHaveAttribute('aria-disabled', 'true')
  await expect.element(screen.getByRole('link', { name: 'Next year' })).not.toHaveAttribute('aria-disabled')
})

test('a period or a date that the contract refuses falls back to the Day of today', async () => {
  // Arrange
  const today = todayAt(browserTimeZone(), new Date())
  const todayPath = `/api/v1/summaries/Day/${today.toString()}`
  const empty = week({ id: null, period: 'Day', periodDate: today.toString(), periodLastDate: today.toString(), status: 'Missing', content: '', sourceCount: 0 })
  const { screen } = await openPage('/summaries?period=Fortnight&date=2026-02-30', [open(todayPath, empty)])

  // Assert
  await expect.element(screen.getByText('No Sessions in this period.')).toBeVisible()
  await expect.element(screen.getByRole('tab', { name: 'Day' })).toHaveAttribute('aria-selected', 'true')
  expect(sentCount(`POST ${todayPath}/generate`)).toBe(1)
})

test('at 390 px, each tab, link, and button of the period is at least 44 px high and wide', async () => {
  // Arrange
  await page.viewport(390, 844)
  const stale = week({ status: 'Stale', content: 'Old words.' })
  const { screen } = await openPage('/summaries?period=Week&date=2026-06-03', [open(weekPath, stale)])
  await expect.element(screen.getByText('Old words.')).toBeVisible()

  // Act
  const controls = [
    ...['Day', 'Week', 'Month', 'Quarter', 'Year'].map((name) => screen.getByRole('tab', { name })),
    screen.getByRole('link', { name: 'Previous week' }),
    screen.getByRole('link', { name: 'Today' }),
    screen.getByRole('link', { name: 'Next week' }),
    screen.getByRole('button', { name: 'Regenerate' }),
  ]

  // Assert
  for (const control of controls) {
    const box = control.element().getBoundingClientRect()
    expect(box.height, control.element().textContent ?? '').toBeGreaterThanOrEqual(44)
    expect(box.width, control.element().textContent ?? '').toBeGreaterThanOrEqual(44)
  }
})
