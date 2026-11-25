import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { createMemoryHistory, createRouter, RouterProvider } from '@tanstack/react-router'
import { expect, test } from 'vitest'
import { commands, page, userEvent } from 'vitest/browser'
import { render } from 'vitest-browser-react'

import type { SummaryDto } from '@/api/generated/models/types/summary-dto'
import type { TranscriptionEngineDto } from '@/api/generated/models/types/transcription-engine-dto'
import type { UserSettingDto } from '@/api/generated/models/types/user-setting-dto'
import { parseTimeZone, todayAt } from '@/lib/zoned-time'
import { routeTree } from '@/routeTree.gen'
import type { Answer } from '@/test/answer'
import { signedInMember } from '@/test/session'

const setting: UserSettingDto = {
  timeZoneMode: 'Fixed',
  timeZone: 'Europe/Lisbon',
  timeZoneIsKnown: true,
  locationCaptureEnabled: false,
  requirePeopleProposalApproval: false,
  requireImportReview: true,
  removeFillerWords: true,
  writeNumbersAsDigits: true,
  version: 1,
}

const engine: TranscriptionEngineDto = {
  engine: 'Local',
  reason: 'NoProviderChosen',
  text: 'The server makes the text, and the audio stays on the server. An Admin can send the audio to a connected OpenAI-compatible provider instead.',
}

async function openAbout(answers: Answer[]) {
  await page.viewport(1280, 800)
  await commands.answerRequests([...signedInMember, ...answers])
  const router = createRouter({ routeTree, history: createMemoryHistory({ initialEntries: ['/about'] }) })
  const screen = await render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  )
  await expect.element(screen.getByRole('heading', { level: 1, name: 'About' })).toBeVisible()
  return { screen, router }
}

test('Summaries in the main navigation opens the Summary of today', async () => {
  // Arrange
  const today = todayAt(parseTimeZone('Europe/Lisbon'), new Date()).toString()
  const day: SummaryDto = { id: null, period: 'Day', periodDate: today, periodLastDate: today, status: 'Missing', failure: 'None', content: '', sourceCount: 0 }
  const { screen, router } = await openAbout([
    { method: 'GET', path: '/api/v1/me/settings', body: setting },
    { method: 'POST', path: `/api/v1/summaries/Day/${today}/generate`, body: day },
  ])

  // Act
  await userEvent.click(screen.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Summaries' }))

  // Assert
  await expect.element(screen.getByRole('heading', { level: 1, name: 'Summaries' })).toBeVisible()
  await expect.element(screen.getByText('No Sessions in this period.')).toBeVisible()
  expect(router.state.location.pathname).toBe('/summaries')
})

test('Transcription in the main navigation opens the page where the audio goes', async () => {
  // Arrange
  const { screen, router } = await openAbout([{ method: 'GET', path: '/api/v1/transcription-engine', body: engine }])

  // Act
  await userEvent.click(screen.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Transcription' }))

  // Assert
  await expect.element(screen.getByRole('heading', { level: 1, name: 'Transcription' })).toBeVisible()
  await expect.element(screen.getByText(engine.text)).toBeVisible()
  expect(router.state.location.pathname).toBe('/transcription-engine')
})
