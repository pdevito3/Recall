import { onlineManager, QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { createMemoryHistory, createRootRoute, createRoute, createRouter, RouterProvider } from '@tanstack/react-router'
import { afterEach, expect, onTestFinished, test, vi } from 'vitest'
import { commands, page, userEvent } from 'vitest/browser'
import { cleanup, render } from 'vitest-browser-react'

import type { RecordingDto } from '@/api/generated/models/types/recording-dto'
import type { SessionDto } from '@/api/generated/models/types/session-dto'
// The app sends each request through this client, and it gives the words of a failure.
import '@/lib/api-client'
import type { Answer } from '@/test/answer'
import { member, signedInMember } from '@/test/session'

import { addCapture, capturesOf, removeCapture, storeSlice, type StoredCapture } from '../audio-store'
import { useSendStoredAudio } from '../use-send-stored-audio'
import { MicCard } from './mic-card'

const doc = (words: string) => JSON.stringify({ type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: words }] }] })

const spoken = 'Walked to the lake after work and met Sam for coffee.'

const entry: SessionDto = {
  id: 'ses_1',
  clientSessionId: 'c1',
  startedAt: '2026-11-10T08:00:00Z',
  journalingDay: '2026-11-10',
  rawText: doc(spoken),
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

const recording = (change: Partial<RecordingDto>): RecordingDto => ({
  id: 'rec_1',
  sessionId: 'ses_1',
  clientRecordingId: 'cap_1',
  format: 'Webm',
  engine: 'Local',
  status: 'Received',
  problem: 'None',
  problemText: null,
  storedSlices: [],
  byteLength: 0n,
  transcribedMs: 0n,
  version: 1,
  ...change,
})

// The contract types an int64 as a bigint, and the wire has a JSON number.
const recordingAnswer = (method: Answer['method'], path: string, change: Partial<RecordingDto>, status = 200): Answer => ({
  method,
  path,
  status,
  contentType: 'application/json',
  body: JSON.stringify(recording(change), (_, value: unknown) => (typeof value === 'bigint' ? Number(value) : value)),
})

const onServer: Answer[] = [
  ...signedInMember,
  { method: 'GET', path: '/api/v1/transcription-engine', body: { engine: 'Local', reason: 'NoProviderChosen', text: 'The server makes the text.' } },
  { method: 'GET', path: '/api/v1/sessions/ses_1', body: entry },
]

// The card and the queue of the root layout, as on the journal home.
function Home() {
  useSendStoredAudio()
  return <MicCard />
}

async function openHome(answers: Answer[]) {
  await commands.answerRequests([...onServer, ...answers])
  const rootRoute = createRootRoute({ component: Home })
  const routeTree = rootRoute.addChildren([
    createRoute({ getParentRoute: () => rootRoute, path: '/sessions/$id' }),
    createRoute({ getParentRoute: () => rootRoute, path: '/transcription-engine' }),
  ])
  const router = createRouter({ routeTree, history: createMemoryHistory({ initialEntries: ['/'] }) })
  return render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  )
}

/** The live status of the card holds these words, as a screen reader says them. */
const cardSays = (screen: Awaited<ReturnType<typeof openHome>>, words: string, timeout = 5_000) =>
  expect.element(screen.getByRole('status').getByText(words, { exact: false }), { timeout }).toBeInTheDocument()

// Speech is a tone here: the test fakes the source of the mic, and the recorder, the store, and the queue are real.
function micGivesATone() {
  return vi.spyOn(navigator.mediaDevices, 'getUserMedia').mockImplementation(async () => {
    const context = new AudioContext()
    const tone = context.createOscillator()
    const out = context.createMediaStreamDestination()
    tone.connect(out)
    tone.start()
    return out.stream
  })
}

async function aFinishedCapture(change: Partial<StoredCapture> = {}) {
  await addCapture({
    clientRecordingId: 'cap_1',
    owner: member.identifier,
    clientSessionId: 'c1',
    startedAt: new Date(Date.now() - 60_000).toISOString(),
    format: 'Webm',
    storedSlices: 3,
    storedBytes: 3_000,
    sentSlices: 3,
    durationMs: 3_000,
    isEnded: true,
    sessionId: 'ses_1',
    recordingId: 'rec_1',
    isFinished: true,
    problem: null,
    touchedAt: Date.now(),
    ...change,
  })
}

// The audio store outlives a test, as it outlives a page. The page goes first, or it reads the empty store and calls again.
afterEach(async () => {
  await cleanup()
  for (const capture of await capturesOf(member.identifier)) await removeCapture(capture.clientRecordingId)
  vi.restoreAllMocks()
})

test('one tap records into a new entry, and the card follows the audio until the words are in', { timeout: 30_000 }, async () => {
  // Arrange
  await page.viewport(390, 800)
  micGivesATone()
  const slices = Array.from({ length: 10 }, (_, index): Answer => ({ method: 'PUT', path: `/api/v1/recordings/rec_1/slices/${index}`, body: {} }))
  const screen = await openHome([
    { method: 'POST', path: '/api/v1/sessions', status: 201, body: entry },
    recordingAnswer('POST', '/api/v1/sessions/ses_1/recordings', { status: 'Receiving' }, 201),
    ...slices,
    recordingAnswer('POST', '/api/v1/recordings/rec_1/finish', { status: 'Received' }),
    recordingAnswer('GET', '/api/v1/recordings/rec_1', { status: 'Transcribing' }),
  ])
  await expect.element(screen.getByText('Transcribe on: This server', { exact: false })).toBeVisible()
  await expect.element(screen.getByRole('link', { name: 'Change' })).toHaveAttribute('href', '/transcription-engine')

  // Act
  await userEvent.click(screen.getByRole('button', { name: 'Record' }))

  // Assert
  await cardSays(screen,'Recording')
  await expect.poll(async () => (await capturesOf(member.identifier))[0]?.storedSlices ?? 0, { timeout: 5_000 }).toBeGreaterThanOrEqual(2)
  expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(390)

  await userEvent.click(screen.getByRole('button', { name: 'Stop recording' }))
  await cardSays(screen,'Writing the words', 10_000)
  const sent = (await commands.takeSentRequests()).map(({ method, path }) => `${method} ${path}`)
  expect(sent.filter((call) => call === 'POST /api/v1/sessions')).toHaveLength(1)
  expect(sent[sent.length - 1]).toBe('POST /api/v1/recordings/rec_1/finish')
  expect((await capturesOf(member.identifier))[0]).toMatchObject({ isFinished: true, sentSlices: (await capturesOf(member.identifier))[0].storedSlices })

  await commands.answerRequests([...onServer, recordingAnswer('GET', '/api/v1/recordings/rec_1', { status: 'Transcribed' })])
  await cardSays(screen,'Your words are in', 10_000)
  await expect.element(screen.getByText(spoken)).toBeVisible()
  await expect.element(screen.getByRole('link', { name: 'Open entry' })).toHaveAttribute('href', '/sessions/ses_1')
})

test('a failed transcription keeps the audio on the server, and Try again starts it again', async () => {
  // Arrange
  await aFinishedCapture()
  const screen = await openHome([recordingAnswer('GET', '/api/v1/recordings/rec_1', { status: 'Failed', problem: 'NoWordsFound', problemText: 'No speech was found in the audio.' })])
  await cardSays(screen,'No words came from this audio')
  await cardSays(screen,'No speech was found in the audio. The audio is safe on the server.')
  await commands.answerRequests([...onServer, recordingAnswer('POST', '/api/v1/recordings/rec_1/retry', { status: 'Received' }), recordingAnswer('GET', '/api/v1/recordings/rec_1', { status: 'Received' })])

  // Act
  await userEvent.click(screen.getByRole('button', { name: 'Try again' }))

  // Assert
  await cardSays(screen,'Writing the words')
  expect((await commands.takeSentRequests()).map(({ method, path }) => `${method} ${path}`)).toContain('POST /api/v1/recordings/rec_1/retry')
})

test('with no network the card says that the audio is safe in this browser and waits', async () => {
  // Arrange
  const screen = await openHome([])
  await cardSays(screen, 'Tap to record')
  onlineManager.setOnline(false)
  onTestFinished(() => onlineManager.setOnline(true))

  // Act
  await aFinishedCapture({ sessionId: null, recordingId: null, sentSlices: 0, isFinished: false, durationMs: 65_000 })

  // Assert
  await cardSays(screen,'No network. The upload waits')
  await cardSays(screen, '1:05 of audio is safe in this browser.')
  expect(await commands.takeSentRequests()).toEqual([])
  // The queue of this page sends when the network comes back, so it stops before the test goes online again.
  await screen.unmount()
})

test('a refusal of the server shows its words, and Send again sends the audio again', async () => {
  // Arrange
  await aFinishedCapture({ storedSlices: 0, storedBytes: 0, sentSlices: 0, isFinished: false, problem: { kind: 'refused', text: 'The audio is larger than 8 MiB.' } })
  await storeSlice('cap_1', 0, new Uint8Array([1, 2, 3]).buffer, 1_000)
  const screen = await openHome([
    { method: 'PUT', path: '/api/v1/recordings/rec_1/slices/0', body: {} },
    recordingAnswer('POST', '/api/v1/recordings/rec_1/finish', { status: 'Received' }),
    recordingAnswer('GET', '/api/v1/recordings/rec_1', { status: 'Received' }),
  ])
  await cardSays(screen, 'The audio is larger than 8 MiB. The audio stays in this browser.')
  expect(await commands.takeSentRequests()).toEqual([])

  // Act
  await userEvent.click(screen.getByRole('button', { name: 'Send again' }))

  // Assert
  await cardSays(screen, 'Writing the words')
  expect((await commands.takeSentRequests()).map(({ method, path }) => `${method} ${path}`)).toEqual(['PUT /api/v1/recordings/rec_1/slices/0', 'POST /api/v1/recordings/rec_1/finish'])
})

test('on plain HTTP the card says that the mic needs HTTPS, and the mic stays off', async () => {
  // Arrange
  vi.spyOn(window, 'isSecureContext', 'get').mockReturnValue(false)

  // Act
  const screen = await openHome([])

  // Assert
  await cardSays(screen,'The mic needs HTTPS')
  await expect.element(screen.getByRole('button', { name: 'Record' })).toBeDisabled()
  await expect.element(screen.getByRole('link', { name: 'Turn on HTTPS' })).toBeVisible()
})

// Last, because the mic state of the tab keeps the denial until the next tap.
test('a denied mic says how to allow it, and lets the person type instead', async () => {
  // Arrange
  vi.spyOn(navigator.mediaDevices, 'getUserMedia').mockRejectedValue(new DOMException('Permission denied', 'NotAllowedError'))
  const screen = await openHome([])

  // Act
  await userEvent.click(screen.getByRole('button', { name: 'Record' }))

  // Assert
  await cardSays(screen,'The browser blocks the mic')
  await cardSays(screen,'set Microphone to Allow, then try again. You can type your entry now.')
  await expect.element(screen.getByRole('button', { name: 'Type instead' })).toBeVisible()
  await expect.element(screen.getByRole('button', { name: 'Try again' })).toBeEnabled()
  expect(await capturesOf(member.identifier)).toEqual([])
})
