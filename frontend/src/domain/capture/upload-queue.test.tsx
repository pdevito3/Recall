import { onlineManager, QueryClient } from '@tanstack/react-query'
import { afterEach, expect, test } from 'vitest'
import { commands } from 'vitest/browser'

import type { RecordingDto } from '@/api/generated/models/types/recording-dto'
import { SessionKeys } from '@/domain/sessions/apis/session.keys'
// The app sends each call through this client, which turns a refusal into the Problem Details of the Server.
import '@/lib/api-client'
import { newUuid } from '@/lib/new-uuid'
import type { Answer } from '@/test/answer'

import { addCapture, capturesOf, endCapture, readSlice, storeSlice, subscribeToAudioStore, type StoredCapture } from './audio-store'
import { endOnServer, sendStoredAudioOf, tryAgainToSend } from './upload-queue'

// The queue runs against the real IndexedDB of the test browser. Each test has its own owner, so the captures of one test
// are not in the queue of the next.
let owner = ''
let stopQueue = () => undefined as void

afterEach(() => {
  stopQueue()
  onlineManager.setOnline(true)
})

const recording = (status: RecordingDto['status']): RecordingDto => ({
  id: 'rec_1',
  sessionId: 'ses_1',
  clientRecordingId: 'cap_1',
  format: 'Webm',
  engine: 'Local',
  status,
  problem: 'None',
  problemText: null,
  storedSlices: [0, 1],
  byteLength: 7n,
  transcribedMs: 0n,
  version: 1,
})

const asJson = (body: unknown) => JSON.stringify(body, (_, value: unknown) => (typeof value === 'bigint' ? Number(value) : value))

const serverTakesAll: Answer[] = [
  { method: 'POST', path: '/api/v1/sessions', status: 201, body: { id: 'ses_1' } },
  { method: 'POST', path: '/api/v1/sessions/ses_1/recordings', status: 201, contentType: 'application/json', body: asJson(recording('Receiving')) },
  { method: 'PUT', path: '/api/v1/recordings/rec_1/slices/0', contentType: 'application/json', body: '{}' },
  { method: 'PUT', path: '/api/v1/recordings/rec_1/slices/1', contentType: 'application/json', body: '{}' },
  { method: 'POST', path: '/api/v1/recordings/rec_1/finish', contentType: 'application/json', body: asJson(recording('Received')) },
]

async function aStoredCapture(changes: Partial<StoredCapture> = {}, slices: string[] = ['abc', 'defg']) {
  owner = `owner_${newUuid()}`
  const capture: StoredCapture = {
    clientRecordingId: `cap_${newUuid()}`,
    owner,
    clientSessionId: newUuid(),
    startedAt: '2026-11-10T08:00:00.000Z',
    timeZone: 'Asia/Tokyo',
    format: 'Webm',
    storedSlices: 0,
    storedBytes: 0,
    sentSlices: 0,
    durationMs: 0,
    isEnded: false,
    sessionId: null,
    recordingId: null,
    isFinished: false,
    problem: null,
    touchedAt: Date.now(),
    ...changes,
  }
  await addCapture(capture)
  for (const [index, words] of slices.entries()) await storeSlice(capture.clientRecordingId, index + capture.storedSlices, new TextEncoder().encode(words).buffer, 1_000)
  return capture
}

const startQueue = () => {
  stopQueue = sendStoredAudioOf(owner, new QueryClient())
}

const theCapture = async () => (await capturesOf(owner))[0]

const sentPaths = async () => (await commands.takeSentRequests()).map(({ method, path }) => `${method} ${path}`)

test('@REC-30 the queue makes the entry and the recording, sends each slice in order, and finishes with the count, the bytes, and the taps', async () => {
  // Arrange
  await commands.answerRequests(serverTakesAll)
  const capture = await aStoredCapture({ paragraphTapsMs: [1_234, 5_678] })
  await endCapture(capture.clientRecordingId)

  // Act
  startQueue()

  // Assert
  await expect.poll(async () => (await theCapture())?.isFinished, { timeout: 5_000 }).toBe(true)
  const sent = await commands.takeSentRequests()
  expect(sent.map(({ method, path }) => `${method} ${path}`)).toEqual([
    'POST /api/v1/sessions',
    'POST /api/v1/sessions/ses_1/recordings',
    'PUT /api/v1/recordings/rec_1/slices/0',
    'PUT /api/v1/recordings/rec_1/slices/1',
    'POST /api/v1/recordings/rec_1/finish',
  ])
  expect(sent[0].body).toEqual({ clientSessionId: capture.clientSessionId, startedAt: capture.startedAt, timeZone: 'Asia/Tokyo' })
  expect(sent[1].body).toEqual({ clientRecordingId: capture.clientRecordingId, format: 'Webm' })
  expect(sent[4].body).toEqual({ sliceCount: 2, byteLength: 7, paragraphTapsMs: [1_234, 5_678] })
  expect(await readSlice(capture.clientRecordingId, 0)).toBeNull()
  expect(await readSlice(capture.clientRecordingId, 1)).toBeNull()
})

test('a slice stays in the browser until the Server confirms it, and the queue sends it again after a failure', async () => {
  // Arrange
  await commands.answerRequests([...serverTakesAll, { method: 'PUT', path: '/api/v1/recordings/rec_1/slices/1', status: 503, body: { status: 503, title: 'Busy' } }])
  const capture = await aStoredCapture()
  await endCapture(capture.clientRecordingId)

  // Act
  startQueue()

  // Assert
  await expect.poll(async () => (await theCapture())?.problem, { timeout: 5_000 }).toEqual({ kind: 'serverDown' })
  expect(await readSlice(capture.clientRecordingId, 0)).toBeNull()
  expect(await readSlice(capture.clientRecordingId, 1)).not.toBeNull()
  expect((await theCapture())?.sentSlices).toBe(1)

  await commands.answerRequests(serverTakesAll)
  await expect.poll(async () => (await theCapture())?.isFinished, { timeout: 5_000 }).toBe(true)
  expect(await readSlice(capture.clientRecordingId, 1)).toBeNull()
})

test('a new queue, as after a reload, goes on from the first slice that the Server did not confirm', async () => {
  // Arrange
  await commands.answerRequests(serverTakesAll)
  const capture = await aStoredCapture({ sessionId: 'ses_1', recordingId: 'rec_1', storedSlices: 1, storedBytes: 3, sentSlices: 1, isEnded: true }, ['defg'])
  await endCapture(capture.clientRecordingId)

  // Act
  startQueue()

  // Assert
  await expect.poll(async () => (await theCapture())?.isFinished, { timeout: 5_000 }).toBe(true)
  expect(await sentPaths()).toEqual(['PUT /api/v1/recordings/rec_1/slices/1', 'POST /api/v1/recordings/rec_1/finish'])
})

test('the queue finishes only after the recorder stops, and sends each slice while it records', async () => {
  // Arrange
  await commands.answerRequests(serverTakesAll)
  const capture = await aStoredCapture({}, ['abc'])
  // A recorder holds the recording lock of its capture, and a capture with no holder is one of a closed tab.
  let stopRecorder = () => undefined as void
  const recorderStopped = new Promise<void>((resolve) => (stopRecorder = resolve))
  await new Promise<void>((held) => void navigator.locks.request(`recall.capture.recording.${capture.clientRecordingId}`, () => (held(), recorderStopped)))

  // Act
  startQueue()

  // Assert
  await expect.poll(async () => (await theCapture())?.sentSlices, { timeout: 5_000 }).toBe(1)
  await storeSlice(capture.clientRecordingId, 1, new TextEncoder().encode('defg').buffer, 1_000)
  await expect.poll(async () => (await theCapture())?.sentSlices, { timeout: 5_000 }).toBe(2)
  expect(await sentPaths()).not.toContain('POST /api/v1/recordings/rec_1/finish')

  await endCapture(capture.clientRecordingId)
  stopRecorder()
  await expect.poll(async () => (await theCapture())?.isFinished, { timeout: 5_000 }).toBe(true)
})

test('the network that comes back while the queue notes that it is offline still wakes the queue', async () => {
  // Arrange
  await commands.answerRequests(serverTakesAll)
  const capture = await aStoredCapture()
  await endCapture(capture.clientRecordingId)
  onlineManager.setOnline(false)
  // The first change after the start is the offline note of the queue. The network comes back in that moment, after the
  // queue looked at the network and before it waits for news.
  const stopListening = subscribeToAudioStore(() => {
    stopListening()
    onlineManager.setOnline(true)
  })

  // Act
  startQueue()

  // Assert
  await expect.poll(async () => (await theCapture())?.isFinished, { timeout: 5_000 }).toBe(true)
})

test('the audio of a tab that closed while it recorded is ended and sent', async () => {
  // Arrange
  await commands.answerRequests(serverTakesAll)
  await aStoredCapture({ touchedAt: Date.now() - 60_000 })

  // Act
  startQueue()

  // Assert
  await expect.poll(async () => (await theCapture())?.isFinished, { timeout: 5_000 }).toBe(true)
  expect((await theCapture())?.isEnded).toBe(true)
})

test('the audio goes to a new entry when the person deleted the entry before its audio came', async () => {
  // Arrange
  await commands.answerRequests([...serverTakesAll, { method: 'POST', path: '/api/v1/sessions', status: 409, body: { status: 409, title: 'Deleted' } }])
  const capture = await aStoredCapture()
  await endCapture(capture.clientRecordingId)

  // Act
  startQueue()

  // Assert
  await expect.poll(async () => (await theCapture())?.clientSessionId, { timeout: 5_000 }).not.toBe(capture.clientSessionId)
  await commands.answerRequests(serverTakesAll)
  await expect.poll(async () => (await theCapture())?.isFinished, { timeout: 5_000 }).toBe(true)
  const entries = (await commands.takeSentRequests()).filter(({ path }) => path === '/api/v1/sessions')
  expect(entries[0].body).toMatchObject({ clientSessionId: capture.clientSessionId })
  expect(entries[entries.length - 1].body).not.toMatchObject({ clientSessionId: capture.clientSessionId })
})

test('the browser lets the audio go when the person deleted the entry, and its recording, during the upload, and the timeline reads again', async () => {
  // Arrange
  await commands.answerRequests([...serverTakesAll, { method: 'PUT', path: '/api/v1/recordings/rec_1/slices/1', status: 404, body: { status: 404, title: 'Not found' } }])
  const capture = await aStoredCapture({ sessionId: 'ses_1', recordingId: 'rec_1' })
  await endCapture(capture.clientRecordingId)
  const queryClient = new QueryClient()
  queryClient.setQueryData(SessionKeys.timeline(), { pages: [], pageParams: [] })

  // Act
  stopQueue = sendStoredAudioOf(owner, queryClient)

  // Assert
  await expect.poll(async () => (await capturesOf(owner)).length, { timeout: 5_000 }).toBe(0)
  expect(await readSlice(capture.clientRecordingId, 1)).toBeNull()
  // The timeline of this tab still shows the deleted entry until it reads again.
  expect(queryClient.getQueryState(SessionKeys.timeline())?.isInvalidated).toBe(true)
})

test('a refusal because the server removed the audio keeps the audio and offers no send again', async () => {
  // Arrange
  const detail = 'The server removed the audio of this recording, so a late part cannot join it.'
  await commands.answerRequests([
    ...serverTakesAll,
    { method: 'PUT', path: '/api/v1/recordings/rec_1/slices/0', status: 409, body: { status: 409, title: 'Conflict', detail, errors: { audio: [detail] } } },
  ])
  const capture = await aStoredCapture()
  await endCapture(capture.clientRecordingId)

  // Act
  startQueue()

  // Assert
  await expect.poll(async () => (await theCapture())?.problem, { timeout: 5_000 }).toEqual({ kind: 'refused', text: detail, canSendAgain: false })
  expect(await readSlice(capture.clientRecordingId, 0)).not.toBeNull()
})

test('a refusal keeps the audio and stops the queue until the person sends again', async () => {
  // Arrange
  await commands.answerRequests([...serverTakesAll, { method: 'PUT', path: '/api/v1/recordings/rec_1/slices/0', status: 422, body: { status: 422, title: 'Invalid', detail: 'The slice is too large.' } }])
  const capture = await aStoredCapture()
  await endCapture(capture.clientRecordingId)

  // Act
  startQueue()

  // Assert
  await expect.poll(async () => (await theCapture())?.problem?.kind, { timeout: 5_000 }).toBe('refused')
  expect(await readSlice(capture.clientRecordingId, 0)).not.toBeNull()

  await commands.answerRequests(serverTakesAll)
  await tryAgainToSend(capture.clientRecordingId)
  await expect.poll(async () => (await theCapture())?.isFinished, { timeout: 5_000 }).toBe(true)
})

test('@REC-30 the end of a capture on the server finishes it with the stored taps', async () => {
  // Arrange
  await commands.answerRequests([
    { method: 'GET', path: '/api/v1/recordings/rec_1', contentType: 'application/json', body: asJson(recording('Receiving')) },
    { method: 'POST', path: '/api/v1/recordings/rec_1/finish', contentType: 'application/json', body: asJson(recording('Received')) },
  ])
  const capture = await aStoredCapture({ sessionId: 'ses_1', recordingId: 'rec_1', paragraphTapsMs: [800, 4_100] })

  // Act
  await endOnServer([capture])

  // Assert
  const sent = await commands.takeSentRequests()
  const finish = sent.find(({ method, path }) => method === 'POST' && path === '/api/v1/recordings/rec_1/finish')
  expect(finish?.body).toEqual({ sliceCount: 2, byteLength: 7, paragraphTapsMs: [800, 4_100] })
})
