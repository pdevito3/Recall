import { defaultScheduler, notifyManager, QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { createMemoryHistory, createRootRoute, createRoute, createRouter, Outlet, RouterProvider } from '@tanstack/react-router'
import { expect, onTestFinished, test, vi } from 'vitest'
import { commands, userEvent } from 'vitest/browser'
import { render } from 'vitest-browser-react'

import type { SessionPageDto } from '@/api/generated/models/types/session-page-dto'
import type { SessionSummaryDto } from '@/api/generated/models/types/session-summary-dto'
import { PersonKeys } from '@/domain/people/apis/person.keys'
import { parseTimeZone, todayAt } from '@/lib/zoned-time'

import { dayHeading } from '../timeline-days'
import { Timeline } from './timeline'

const zone = parseTimeZone('Pacific/Pago_Pago')
const today = todayAt(zone, new Date())
const fiveDaysAgo = today.subtract({ days: 5 })
const sixDaysAgo = today.subtract({ days: 6 })
const headingOf = (day: typeof today) => dayHeading(day, today)

const entry = (change: Partial<SessionSummaryDto>): SessionSummaryDto => ({
  id: 'ses_1',
  clientSessionId: 'c1',
  startedAt: `${fiveDaysAgo.toString()}T23:30:00Z`,
  journalingDay: fiveDaysAgo.toString(),
  preview: 'Walked to the lake after work and met Sam for coffee.',
  cleanupStatus: 'NotRun',
  cleanupFailure: 'None',
  activity: 'None',
  moods: [],
  topics: [],
  personIds: [],
  version: 1,
  metadataVersion: 0,
  ...change,
})

const page: SessionPageDto = {
  items: [
    entry({}),
    entry({ id: 'ses_2', clientSessionId: 'c2', startedAt: `${sixDaysAgo.toString()}T23:00:00Z`, journalingDay: sixDaysAgo.toString(), preview: 'A long call with my sister about the move.' }),
  ],
}

async function renderJournal(answer: SessionPageDto = page) {
  await commands.answerRequests([{ method: 'GET', path: '/api/v1/sessions', body: answer }])
  const rootRoute = createRootRoute({
    component: () => (
      <>
        <Timeline zone={zone} hiddenSessionId={null} />
        <Outlet />
      </>
    ),
  })
  const journalRoute = createRoute({ getParentRoute: () => rootRoute, path: '/' })
  const summariesRoute = createRoute({ getParentRoute: () => rootRoute, path: '/summaries', component: () => <h1>Summaries</h1> })
  const entryRoute = createRoute({ getParentRoute: () => rootRoute, path: '/sessions/$id' })
  const router = createRouter({
    routeTree: rootRoute.addChildren([journalRoute, summariesRoute, entryRoute]),
    history: createMemoryHistory({ initialEntries: ['/'] }),
  })
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  // The directory gives the names of the people of a tag, and no entry here has one.
  client.setQueryData(PersonKeys.directory(), [])
  const screen = await render(
    <QueryClientProvider client={client}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  )
  return { screen, router }
}

test('the Summary link of a day of the Journal opens the Day Summary of that day', async () => {
  // Arrange
  const { screen, router } = await renderJournal()
  const day = screen.getByRole('region', { name: headingOf(fiveDaysAgo) })
  await expect.element(day.getByText('Walked to the lake after work and met Sam for coffee.')).toBeVisible()

  // Act
  await userEvent.click(day.getByRole('link', { name: `Summary of ${headingOf(fiveDaysAgo)}` }))

  // Assert
  await expect.element(screen.getByRole('heading', { level: 1, name: 'Summaries' })).toBeVisible()
  expect(router.state.location.pathname).toBe('/summaries')
  expect(router.state.location.search).toEqual({ period: 'Day', date: fiveDaysAgo.toString() })
})

test('each day of the Journal links to the Summary of its own day', async () => {
  // Arrange
  const { screen } = await renderJournal()

  // Assert
  await expect.element(screen.getByRole('link', { name: `Summary of ${headingOf(fiveDaysAgo)}` })).toBeVisible()
  await expect
    .element(screen.getByRole('link', { name: `Summary of ${headingOf(sixDaysAgo)}` }))
    .toHaveAttribute('href', `/summaries?period=Day&date=${sixDaysAgo.toString()}`)
})

test('a fast load shows the entries with no flash of the loading words', async () => {
  // Arrange
  // The test moves the clock of the page, so the load takes 300 ms on a busy machine too.
  // Query tells of the answer in a timer, so it tells in a microtask here. A wait such as expect.element moves
  // a fake clock, so the observer tells when the entries show.
  const firstPage = '/api/v1/sessions?pageSize=50'
  const fastLoadMs = 300
  await commands.holdRequests(firstPage)
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
  notifyManager.setScheduler(queueMicrotask)
  onTestFinished(() => {
    notifyManager.setScheduler(defaultScheduler)
    vi.useRealTimers()
  })
  let didTheWordsShow = false
  const entriesShow = new Promise<void>((resolve) => {
    const watch = new MutationObserver(() => {
      const text = document.body.textContent ?? ''
      didTheWordsShow ||= text.includes('Loading your entries')
      if (text.includes('Walked to the lake after work and met Sam for coffee.')) {
        watch.disconnect()
        resolve()
      }
    })
    watch.observe(document.body, { childList: true, subtree: true, characterData: true })
  })

  // Act
  const { screen } = await renderJournal()
  vi.advanceTimersByTime(fastLoadMs)
  await commands.releaseRequests(firstPage)
  await entriesShow

  // Assert
  await expect.element(screen.getByText('Walked to the lake after work and met Sam for coffee.')).toBeVisible()
  expect(didTheWordsShow).toBe(false)
})

test('a slow load says that the entries load', async () => {
  // Arrange
  const firstPage = '/api/v1/sessions?pageSize=50'
  await commands.holdRequests(firstPage)

  // Act
  const { screen } = await renderJournal()

  // Assert
  await expect.element(screen.getByRole('status', { name: 'Loading your entries' })).toBeVisible()
  await commands.releaseRequests(firstPage)
  await expect.element(screen.getByText('Walked to the lake after work and met Sam for coffee.')).toBeVisible()
})

test('a person with no entries sees that none exist yet, and where to write the first one', async () => {
  // Act
  const { screen } = await renderJournal({ items: [] })

  // Assert
  await expect.element(screen.getByText('No entries yet', { exact: true })).toBeVisible()
  await expect.element(screen.getByText('Your entries show here. Write your first one above.', { exact: true })).toBeVisible()
})
