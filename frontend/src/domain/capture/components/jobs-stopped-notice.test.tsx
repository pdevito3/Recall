import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { expect, test } from 'vitest'
import { commands } from 'vitest/browser'
import { render } from 'vitest-browser-react'

import { JobsStoppedNotice } from './jobs-stopped-notice'

const statusIs = (body: unknown, status = 200) => commands.answerRequests([{ method: 'GET', path: '/api/v1/recordings/status', body, status }])

const renderNotice = () =>
  render(
    <QueryClientProvider client={new QueryClient()}>
      <JobsStoppedNotice />
    </QueryClientProvider>,
  )

test('the page says what the Server says when the jobs stopped, with what to do', async () => {
  await statusIs({ jobsStopped: true, jobsStoppedText: 'The work stopped. The Owner restarts the server.', model: 'Ready' })

  const screen = await renderNotice()

  await expect.element(screen.getByRole('alert')).toHaveTextContent('The work stopped. The Owner restarts the server.')
})

test('the page says nothing while the jobs run', async () => {
  await statusIs({ jobsStopped: false, jobsStoppedText: null, model: 'Ready' })

  const screen = await renderNotice()

  await new Promise((resolve) => setTimeout(resolve, 200))
  await expect.element(screen.getByRole('alert')).not.toBeInTheDocument()
})

test('a person who may not read the status sees no notice', async () => {
  await statusIs({ title: 'Forbidden', status: 403 }, 403)

  const screen = await renderNotice()

  await new Promise((resolve) => setTimeout(resolve, 200))
  await expect.element(screen.getByRole('alert')).not.toBeInTheDocument()
})
