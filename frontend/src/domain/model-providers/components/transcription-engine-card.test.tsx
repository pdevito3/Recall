import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { expect, onTestFinished, test } from 'vitest'
import { commands, page, userEvent } from 'vitest/browser'
import { render } from 'vitest-browser-react'

import type { ModelProviderDto } from '@/api/generated/models/types/model-provider-dto'
import type { TranscriptionEngineDto } from '@/api/generated/models/types/transcription-engine-dto'
// The app sends each request through this client, and it gives the words of a failure.
import '@/lib/api-client'

import { TranscriptionEngineCard } from './transcription-engine-card'

const noProviderTakesAudio: TranscriptionEngineDto = {
  engine: 'Local',
  reason: 'NoProviderTakesAudio',
  text: 'The server makes the text, and the audio stays on the server, because no connected model provider takes audio. Anthropic takes no audio. To send audio out, an Admin connects an OpenAI-compatible provider.',
}

const noProviderChosen: TranscriptionEngineDto = {
  engine: 'Local',
  reason: 'NoProviderChosen',
  text: 'The server makes the text, and the audio stays on the server. An Admin can send the audio to a connected OpenAI-compatible provider instead.',
}

const sentToLocalAi: TranscriptionEngineDto = {
  engine: 'Cloud',
  reason: 'ProviderChosen',
  text: 'The audio of each new Recording goes to Local AI, and Local AI makes the text. Audio that waits from before keeps its engine.',
  modelProviderId: 'mp_2',
  modelProviderName: 'Local AI',
  model: 'whisper-1',
}

const provider = (change: Partial<ModelProviderDto>): ModelProviderDto => ({
  id: 'mp_1',
  kind: 'Anthropic',
  name: 'Claude',
  model: 'claude-sonnet',
  isActive: true,
  keyState: 'Readable',
  version: 1,
  ...change,
})

const claude = provider({})
const localAi = provider({ id: 'mp_2', kind: 'OpenAiCompatible', name: 'Local AI', baseUrl: 'http://localhost:11434/v1', isActive: false })

const engineAnswer = (body: TranscriptionEngineDto) => ({ method: 'GET', path: '/api/v1/transcription-engine', body }) as const
const providersAnswer = (items: ModelProviderDto[]) => ({ method: 'GET', path: '/api/v1/model-providers', body: { items, keyRing: 'Same' } }) as const

async function renderCard(canChoose: boolean, answers: Parameters<typeof commands.answerRequests>[0]) {
  await commands.answerRequests(answers)
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <TranscriptionEngineCard canChoose={canChoose} />
    </QueryClientProvider>,
  )
}

test('with no provider that takes audio, the card says that this server makes the text and why, and an Admin gets no choice', async () => {
  // Arrange
  const screen = await renderCard(true, [engineAnswer(noProviderTakesAudio), providersAnswer([claude])])

  // Assert
  await expect.element(screen.getByText(noProviderTakesAudio.text)).toBeVisible()
  await expect.element(screen.getByText('This server', { exact: true })).toBeVisible()
  await expect.element(screen.getByRole('button', { name: /Model provider for audio/ })).not.toBeInTheDocument()
  await expect.element(screen.getByRole('button', { name: 'Send audio to this provider' })).not.toBeInTheDocument()
})

test('an Admin who sends audio to a provider sends its id and the model, and the card then names the provider and the model', async () => {
  // Arrange
  const screen = await renderCard(true, [
    engineAnswer(noProviderChosen),
    providersAnswer([claude, localAi]),
    { method: 'PUT', path: '/api/v1/transcription-engine', body: sentToLocalAi },
  ])
  await expect.element(screen.getByText(noProviderChosen.text)).toBeVisible()

  // Act
  await screen.getByRole('button', { name: /Model provider for audio/ }).click()
  await expect.element(page.getByRole('option', { name: 'Claude' })).not.toBeInTheDocument()
  await page.getByRole('option', { name: 'Local AI' }).click()
  await userEvent.fill(screen.getByLabelText('Model for audio'), 'whisper-1')
  await commands.answerRequests([engineAnswer(sentToLocalAi), providersAnswer([claude, localAi]), { method: 'PUT', path: '/api/v1/transcription-engine', body: sentToLocalAi }])
  await screen.getByRole('button', { name: 'Send audio to this provider' }).click()

  // Assert
  await expect.element(screen.getByText(sentToLocalAi.text)).toBeVisible()
  await expect.element(screen.getByRole('definition').filter({ hasText: 'Local AI' })).toBeVisible()
  await expect.element(screen.getByRole('definition').filter({ hasText: 'whisper-1' })).toBeVisible()
  expect(await commands.takeSentRequests()).toEqual([
    { method: 'PUT', path: '/api/v1/transcription-engine', body: { modelProviderId: 'mp_2', model: 'whisper-1' } },
  ])
})

test('an Admin who keeps audio on this server sends no provider', async () => {
  // Arrange
  const screen = await renderCard(true, [
    engineAnswer(sentToLocalAi),
    providersAnswer([claude, localAi]),
    { method: 'PUT', path: '/api/v1/transcription-engine', body: noProviderChosen },
  ])
  await expect.element(screen.getByText(sentToLocalAi.text)).toBeVisible()
  await commands.answerRequests([engineAnswer(noProviderChosen), providersAnswer([claude, localAi]), { method: 'PUT', path: '/api/v1/transcription-engine', body: noProviderChosen }])

  // Act
  await screen.getByRole('button', { name: 'Keep audio on this server' }).click()

  // Assert
  await expect.element(screen.getByText(noProviderChosen.text)).toBeVisible()
  await expect.element(screen.getByText('This server', { exact: true })).toBeVisible()
  expect(await commands.takeSentRequests()).toEqual([
    { method: 'PUT', path: '/api/v1/transcription-engine', body: { modelProviderId: null, model: null } },
  ])
})

test('a refusal of the server shows its words above the buttons', async () => {
  // Arrange
  const refusal = 'This kind of model provider takes no audio. Choose an OpenAI-compatible provider.'
  const screen = await renderCard(true, [
    engineAnswer(sentToLocalAi),
    providersAnswer([claude, localAi]),
    {
      method: 'PUT',
      path: '/api/v1/transcription-engine',
      status: 422,
      body: { type: 'https://tools.ietf.org/html/rfc9110#section-15.5.21', title: 'Unprocessable Entity', status: 422, errors: { kind: [refusal] } },
    },
  ])
  await expect.element(screen.getByText(sentToLocalAi.text)).toBeVisible()

  // Act
  await screen.getByRole('button', { name: 'Send audio to this provider' }).click()

  // Assert
  await expect.element(screen.getByRole('alert')).toHaveTextContent(refusal)
})

test('a person who is not an Admin sees the engine and the reason, and no form', async () => {
  // Arrange
  const screen = await renderCard(false, [engineAnswer(sentToLocalAi)])

  // Assert
  await expect.element(screen.getByText(sentToLocalAi.text)).toBeVisible()
  await expect.element(screen.getByRole('definition').filter({ hasText: 'Local AI' })).toBeVisible()
  await expect.element(screen.getByRole('button', { name: 'Send audio to this provider' })).not.toBeInTheDocument()
  await expect.element(screen.getByRole('button', { name: 'Keep audio on this server' })).not.toBeInTheDocument()
})

test('at 320 px with a font of 24 px, each control of the form stays inside the card, with no side scroll', async () => {
  // Arrange
  await page.viewport(320, 640)
  document.documentElement.style.fontSize = '24px'
  onTestFinished(() => {
    document.documentElement.style.fontSize = ''
  })
  await commands.answerRequests([engineAnswer(sentToLocalAi), providersAnswer([claude, localAi])])

  // Act
  const screen = await render(
    // The padding of the content of the layout.
    <div className="p-4">
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <TranscriptionEngineCard canChoose />
      </QueryClientProvider>
    </div>,
  )

  // Assert
  await expect.element(screen.getByRole('button', { name: 'Send audio to this provider' })).toBeVisible()
  const edge = screen.getByRole('heading', { name: 'Audio to text' }).element().closest('[data-slot=card]')?.getBoundingClientRect().right ?? 0
  expect(edge).toBeGreaterThan(0)
  expect(edge).toBeLessThanOrEqual(320)
  for (const name of ['Send audio to this provider', 'Keep audio on this server']) {
    expect(screen.getByRole('button', { name }).element().getBoundingClientRect().right, name).toBeLessThanOrEqual(edge)
  }
  expect(screen.getByRole('textbox', { name: 'Model for audio' }).element().getBoundingClientRect().right).toBeLessThanOrEqual(edge)
  expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(320)
})
