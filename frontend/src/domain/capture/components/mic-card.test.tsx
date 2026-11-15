import { onlineManager, QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { createMemoryHistory, createRootRoute, createRoute, createRouter, RouterProvider } from '@tanstack/react-router'
import { afterEach, expect, onTestFinished, test, vi } from 'vitest'
import { commands, page, userEvent } from 'vitest/browser'
import { cleanup, render } from 'vitest-browser-react'

import type { RecordingDto } from '@/api/generated/models/types/recording-dto'
import type { SessionDto } from '@/api/generated/models/types/session-dto'
import type { UserSettingDto } from '@/api/generated/models/types/user-setting-dto'
// The app sends each request through this client, and it gives the words of a failure.
import '@/lib/api-client'
import { answerAboutUnsentWords, settleUnsentWords } from '@/domain/auth/apis/unsent-words-at-logout'
import { useResumeUnsentDrafts } from '@/domain/sessions/autosave/use-resume-unsent-drafts'
import { createQueryClient } from '@/lib/query-client'
import { notePageUser } from '@/lib/page-user'
import type { Answer } from '@/test/answer'
import { member, signedInMember } from '@/test/session'

import { addCapture, capturesOf, removeCapture, storeSlice, updateCapture, type StoredCapture, type UploadProblem } from '../audio-store'
import { stopRecordingOfThisTab } from '../use-capture'
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

// The content of the layout has a gutter, so the card is narrower than the window.
function HomeInTheGutter() {
  return (
    <div className="p-4 pt-0">
      <Home />
    </div>
  )
}

// The layout also joins the other tabs, so that a sign-out in one tab settles each tab.
function HomeInTheLayout() {
  useResumeUnsentDrafts()
  return <Home />
}

async function openHome(answers: Answer[], { client = new QueryClient({ defaultOptions: { queries: { retry: false } } }), component = Home } = {}) {
  await commands.answerRequests([...onServer, ...answers])
  const rootRoute = createRootRoute({ component })
  const routeTree = rootRoute.addChildren([
    createRoute({ getParentRoute: () => rootRoute, path: '/sessions/$id' }),
    createRoute({ getParentRoute: () => rootRoute, path: '/transcription-engine' }),
  ])
  const router = createRouter({ routeTree, history: createMemoryHistory({ initialEntries: ['/'] }) })
  return render(
    <QueryClientProvider client={client}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  )
}

/** The live status of the card holds these words, as a screen reader says them. */
const cardSays = (screen: Awaited<ReturnType<typeof openHome>>, words: string, timeout = 5_000) =>
  expect.element(screen.getByRole('status').getByText(words, { exact: false }), { timeout }).toBeInTheDocument()

// Speech is a tone here: the test fakes the source of the mic, and the recorder, the store, and the queue are real.
function aTone() {
  const context = new AudioContext()
  const tone = context.createOscillator()
  const out = context.createMediaStreamDestination()
  tone.connect(out)
  tone.start()
  return out.stream
}

function micGivesATone() {
  return vi.spyOn(navigator.mediaDevices, 'getUserMedia').mockImplementation(async () => aTone())
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
  vi.restoreAllMocks()
  for (const capture of await capturesOf(member.identifier)) await removeCapture(capture.clientRecordingId)
})

test.each([390, 1280])('at %i px the card keeps its height while the mic starts and records, so nothing moves under the finger', async (width) => {
  // Arrange
  await page.viewport(width, 800)
  let micStarts: (stream: MediaStream) => void = () => {}
  vi.spyOn(navigator.mediaDevices, 'getUserMedia').mockImplementation(() => new Promise<MediaStream>((resolve) => (micStarts = resolve)))
  const screen = await openHome([])
  await expect.element(screen.getByRole('link', { name: 'Change' })).toBeVisible()
  const card = screen.getByRole('region', { name: 'Record an entry' })
  const idleHeight = card.element().getBoundingClientRect().height

  // Act
  await userEvent.click(screen.getByRole('button', { name: 'Record' }))

  // Assert
  await cardSays(screen, 'Starting the mic')
  const startingHeight = card.element().getBoundingClientRect().height
  // The mic of this page outlives the test, so it starts and stops before the check, and the next test finds it idle. With no network nothing goes out.
  onlineManager.setOnline(false)
  onTestFinished(() => onlineManager.setOnline(true))
  micStarts(aTone())
  await cardSays(screen, 'Each second saves in this browser')
  const recordingHeight = card.element().getBoundingClientRect().height
  await userEvent.click(screen.getByRole('button', { name: 'Stop recording' }))
  await expect.element(screen.getByRole('button', { name: 'Record', exact: true })).toBeInTheDocument()
  await screen.unmount()
  expect(startingHeight).toBe(idleHeight)
  expect(recordingHeight).toBe(idleHeight)
})

test.each([
  { width: 390, fontSize: 16 },
  { width: 1280, fontSize: 16 },
  { width: 320, fontSize: 24 },
])('@REC-30 at $width px with a font of $fontSize px New paragraph shows only while recording, each press says so and is stored, and the card keeps its height and its width', async ({ width, fontSize }) => {
  // Arrange
  await page.viewport(width, 800)
  document.documentElement.style.fontSize = `${fontSize}px`
  onTestFinished(() => {
    document.documentElement.style.fontSize = ''
  })
  micGivesATone()
  const vibrate = vi.spyOn(navigator, 'vibrate').mockReturnValue(true)
  const screen = await openHome([], { component: HomeInTheGutter })
  await expect.element(screen.getByRole('link', { name: 'Change' })).toBeVisible()
  // With no network nothing goes out, so the taps stay in the row of this browser.
  onlineManager.setOnline(false)
  onTestFinished(() => onlineManager.setOnline(true))
  const card = screen.getByRole('region', { name: 'Record an entry' })
  const idleHeight = card.element().getBoundingClientRect().height
  const newParagraph = screen.getByRole('button', { name: 'New paragraph' })
  await expect.element(newParagraph).not.toBeInTheDocument()
  await userEvent.click(screen.getByRole('button', { name: 'Record' }))
  await cardSays(screen, 'Each second saves in this browser')
  await expect.element(newParagraph).toBeVisible()
  const recordingHeight = card.element().getBoundingClientRect().height

  // Act
  await userEvent.click(newParagraph)
  await expect.element(screen.getByRole('status').getByText(/^New paragraph at 0:\d\d\.$/)).toBeInTheDocument()
  await userEvent.click(newParagraph)

  // Assert
  await expect.poll(async () => (await capturesOf(member.identifier))[0]?.paragraphTapsMs?.length).toBe(2)
  await expect.element(card.getByTestId('paragraph-marks')).toHaveTextContent('2')
  expect(vibrate.mock.calls).toEqual([[15], [15]])
  expect(newParagraph.element().getBoundingClientRect().height).toBeGreaterThanOrEqual(44)
  expect(card.element().getBoundingClientRect().height).toBe(idleHeight)
  expect(recordingHeight).toBe(idleHeight)
  expect(card.element().scrollWidth).toBeLessThanOrEqual(card.element().clientWidth)
  expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(width)
  await userEvent.click(screen.getByRole('button', { name: 'Stop recording' }))
  await expect.element(screen.getByRole('button', { name: 'Record', exact: true })).toBeInTheDocument()
  await expect.element(newParagraph).not.toBeInTheDocument()
  await screen.unmount()
})

test('a recording that ends with the focus on New paragraph moves the focus to the mic', async () => {
  // Arrange
  micGivesATone()
  const screen = await openHome([])
  await cardSays(screen, 'Tap to record')
  onlineManager.setOnline(false)
  onTestFinished(() => onlineManager.setOnline(true))
  await userEvent.click(screen.getByRole('button', { name: 'Record' }))
  await cardSays(screen, 'Each second saves in this browser')
  screen.getByRole('button', { name: 'New paragraph' }).element().focus()

  // Act
  await stopRecordingOfThisTab()

  // Assert
  await expect.element(screen.getByRole('button', { name: 'Record', exact: true })).toBeInTheDocument()
  expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Record', exact: true }).element())
  await screen.unmount()
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
  await cardSays(screen, 'Recording')
  await expect.poll(async () => (await capturesOf(member.identifier))[0]?.storedSlices ?? 0, { timeout: 5_000 }).toBeGreaterThanOrEqual(2)
  expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(390)

  await userEvent.click(screen.getByRole('button', { name: 'Stop recording' }))
  await cardSays(screen, 'Writing the words', 10_000)
  const sent = (await commands.takeSentRequests()).map(({ method, path }) => `${method} ${path}`)
  expect(sent.filter((call) => call === 'POST /api/v1/sessions')).toHaveLength(1)
  expect(sent[sent.length - 1]).toBe('POST /api/v1/recordings/rec_1/finish')
  expect((await capturesOf(member.identifier))[0]).toMatchObject({ isFinished: true, sentSlices: (await capturesOf(member.identifier))[0].storedSlices })

  // The timeline of another tab reads again when the words come.
  const otherTab = new BroadcastChannel('recall.timeline')
  onTestFinished(() => otherTab.close())
  const heard = vi.fn()
  otherTab.onmessage = ({ data }) => heard(data)
  await commands.answerRequests([...onServer, recordingAnswer('GET', '/api/v1/recordings/rec_1', { status: 'Transcribed' })])
  await cardSays(screen, 'Your words are in', 10_000)
  await expect.poll(() => heard.mock.calls).toEqual([['words']])
  await expect.element(screen.getByText(spoken)).toBeVisible()
  await expect.element(screen.getByRole('link', { name: 'Open entry' })).toHaveAttribute('href', '/sessions/ses_1')
})

test('a failed transcription keeps the audio on the server, and Try again starts it again', async () => {
  // Arrange
  await aFinishedCapture()
  const noWords = 'The audio is stored, but the server heard no words in it. Listen to it to check, or try again.'
  const screen = await openHome([recordingAnswer('GET', '/api/v1/recordings/rec_1', { status: 'Failed', problem: 'NoWordsFound', problemText: noWords })])
  await cardSays(screen, 'No words came from this audio')
  await cardSays(screen, noWords)
  await commands.answerRequests([...onServer, recordingAnswer('POST', '/api/v1/recordings/rec_1/retry', { status: 'Received' }), recordingAnswer('GET', '/api/v1/recordings/rec_1', { status: 'Received' })])

  // Act
  await userEvent.click(screen.getByRole('button', { name: 'Try again' }))

  // Assert
  await cardSays(screen, 'Writing the words')
  expect((await commands.takeSentRequests()).map(({ method, path }) => `${method} ${path}`)).toContain('POST /api/v1/recordings/rec_1/retry')
})

test('a transcription that failed because the server lost the audio does not say that the audio is safe', async () => {
  // Arrange
  await aFinishedCapture()
  const missing = 'The server cannot find the audio of this recording. Tell the Owner of this server.'

  // Act
  const screen = await openHome([recordingAnswer('GET', '/api/v1/recordings/rec_1', { status: 'Failed', problem: 'AudioMissing', problemText: missing })])

  // Assert
  await cardSays(screen, 'No words came from this audio')
  await cardSays(screen, missing)
  await expect.element(screen.getByText('safe', { exact: false })).not.toBeInTheDocument()
})

test('words that came with a lost part of the audio say that they have a gap, with no check mark', async () => {
  // Arrange
  await aFinishedCapture()
  const lost = 'Part of the audio of this recording was lost on the server, so the words of that part are missing. Tell the Owner of this server.'

  // Act
  const screen = await openHome([recordingAnswer('GET', '/api/v1/recordings/rec_1', { status: 'Transcribed', problemText: lost })])

  // Assert
  await cardSays(screen, 'Your words are in, with a gap')
  await cardSays(screen, lost)
  await expect.element(screen.getByRole('status').getByText('Your words are in, with a gap')).not.toHaveClass(/text-destructive/)
  expect(document.querySelector('section[aria-label="Record an entry"] [role="status"] svg')?.getAttribute('class')).toContain('text-amber-600')
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
  await cardSays(screen, 'No network. The upload waits')
  await cardSays(screen, '1:05 of audio is safe in this browser.')
  expect(await commands.takeSentRequests()).toEqual([])
  // The queue of this page sends when the network comes back, so it stops before the test goes online again.
  await screen.unmount()
})

test('with no network after each slice went, the card says that the server has the audio and only the words wait', async () => {
  // Arrange
  const screen = await openHome([])
  await cardSays(screen, 'Tap to record')
  onlineManager.setOnline(false)
  onTestFinished(() => onlineManager.setOnline(true))

  // Act
  await aFinishedCapture({ isFinished: false, durationMs: 65_000 })

  // Assert
  await cardSays(screen, 'No network. The words wait')
  await cardSays(screen, 'The server has all 1:05 of this audio.')
  expect(await commands.takeSentRequests()).toEqual([])
  await screen.unmount()
})

test('on a phone the icon of a title that wraps sits at the start of its first line', async () => {
  // Arrange
  await page.viewport(390, 800)
  await aFinishedCapture({ isFinished: false, durationMs: 3_000, problem: { kind: 'serverDown' } })

  // Act
  const screen = await openHome([{ method: 'POST', path: '/api/v1/recordings/rec_1/finish', status: 503, body: { status: 503, title: 'Service Unavailable' } }])

  // Assert
  await cardSays(screen, 'The server does not answer. The words wait')
  const title = screen.getByRole('region', { name: 'Record an entry' }).getByText('The server does not answer. The words wait').element()
  const words = [...title.childNodes].find((node) => node.nodeType === Node.TEXT_NODE)!
  const firstLetter = document.createRange()
  firstLetter.setStart(words, 0)
  firstLetter.setEnd(words, 1)
  const letter = firstLetter.getBoundingClientRect()
  const icon = title.querySelector('svg')!.getBoundingClientRect()
  // The title takes two lines at this width.
  expect(title.getBoundingClientRect().height).toBeGreaterThan(letter.height * 1.5)
  expect(letter.left - icon.right).toBeGreaterThanOrEqual(0)
  expect(letter.left - icon.right).toBeLessThanOrEqual(12)
  expect(Math.abs((icon.top + icon.bottom) / 2 - (letter.top + letter.bottom) / 2)).toBeLessThanOrEqual(3)
  expect(icon.width).toBe(20)
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

test('a late part whose recording lost its audio on the server offers Discard and no Send again', async () => {
  // Arrange
  const text = 'The server removed the audio of this recording, so a late part cannot join it.'
  await aFinishedCapture({ storedSlices: 0, storedBytes: 0, sentSlices: 0, isFinished: false, problem: { kind: 'refused', text, canSendAgain: false } })
  await storeSlice('cap_1', 0, new Uint8Array([1, 2, 3]).buffer, 1_000)
  await aRefusedCapture('cap_0', 'rec_0', 180_000, { kind: 'refused', text, canSendAgain: false })

  // Act
  const screen = await openHome([{ method: 'GET', path: '/api/v1/me/settings', body: setting }])

  // Assert
  await cardSays(screen, `${text} The audio stays in this browser.`)
  const card = screen.getByRole('region', { name: 'Record an entry' })
  await expect.element(card.getByRole('button', { name: 'Discard' }).first()).toBeVisible()
  await expect.element(card.getByRole('button', { name: /^Discard the 0:04 recording from / })).toBeVisible()
  expect(card.getByRole('button', { name: /^Send again/ }).elements()).toHaveLength(0)
})

const setting: UserSettingDto = { timeZoneMode: 'Fixed', timeZone: 'Europe/Lisbon', timeZoneIsKnown: true, locationCaptureEnabled: false, requirePeopleProposalApproval: false, removeFillerWords: true, version: 1 }

async function aRefusedCapture(clientRecordingId: string, recordingId: string, agoMs: number, problem: UploadProblem = { kind: 'refused', text: 'The audio is larger than 8 MiB.' }) {
  await aFinishedCapture({
    clientRecordingId,
    recordingId,
    startedAt: new Date(Date.now() - agoMs).toISOString(),
    storedSlices: 0,
    storedBytes: 0,
    sentSlices: 0,
    isFinished: false,
    problem,
  })
  await storeSlice(clientRecordingId, 0, new Uint8Array([1, 2, 3]).buffer, 4_000)
}

test('each older recording that the server refused can go again or go for good', async () => {
  // Arrange
  await aRefusedCapture('cap_0', 'rec_0', 180_000)
  await aRefusedCapture('cap_2', 'rec_2', 120_000)
  await aFinishedCapture()
  const screen = await openHome([
    { method: 'GET', path: '/api/v1/me/settings', body: setting },
    recordingAnswer('GET', '/api/v1/recordings/rec_1', { status: 'Transcribing' }),
    { method: 'PUT', path: '/api/v1/recordings/rec_0/slices/0', body: {} },
    recordingAnswer('POST', '/api/v1/recordings/rec_0/finish', { id: 'rec_0', clientRecordingId: 'cap_0', status: 'Received' }),
    // The server refused the only slice of rec_2, so it holds none.
    recordingAnswer('GET', '/api/v1/recordings/rec_2', { id: 'rec_2', clientRecordingId: 'cap_2', status: 'Receiving', storedSlices: [] }),
    { method: 'DELETE', path: '/api/v1/recordings/rec_2', status: 204 },
    { method: 'GET', path: '/api/v1/sessions/ses_1/recordings', body: [] },
  ])
  await cardSays(screen, 'Writing the words')
  const others = screen.getByRole('list', { name: 'Other recordings in this browser' })
  await expect.element(others.getByRole('listitem')).toHaveLength(2)
  await expect.element(others.getByRole('listitem').first().getByText('The server did not take it: The audio is larger than 8 MiB.')).toBeVisible()
  await expect.element(screen.getByText('2 more recordings are in this browser.')).toBeVisible()
  // A screen reader that lists the buttons tells each row apart.
  const sendAgainNames = others.getByRole('button', { name: /^Send again the 0:04 recording from / }).elements().map((button) => button.getAttribute('aria-label'))
  expect(sendAgainNames).toHaveLength(2)
  expect(new Set(sendAgainNames).size).toBe(2)

  // Act
  await userEvent.click(others.getByRole('listitem').nth(1).getByRole('button', { name: /^Discard the 0:04 recording from / }))
  await userEvent.click(others.getByRole('group', { name: /^Discard the 0:04 recording from / }).getByRole('button', { name: 'Discard audio' }))
  await expect.element(others.getByRole('listitem')).toHaveLength(1)
  await userEvent.click(others.getByRole('listitem').first().getByRole('button', { name: /^Send again the 0:04 recording from / }))

  // Assert
  await expect.element(others).not.toBeInTheDocument()
  expect((await capturesOf(member.identifier)).map(({ clientRecordingId, isFinished }) => ({ clientRecordingId, isFinished }))).toEqual([
    { clientRecordingId: 'cap_0', isFinished: true },
    { clientRecordingId: 'cap_1', isFinished: true },
  ])
  // The entry holds words, so it stays.
  expect((await commands.takeSentRequests()).map(({ method, path }) => `${method} ${path}`)).toEqual([
    'DELETE /api/v1/recordings/rec_2',
    'PUT /api/v1/recordings/rec_0/slices/0',
    'POST /api/v1/recordings/rec_0/finish',
  ])
})

test('a discard of a refused recording leaves no empty recording and no empty entry on the server', async () => {
  // Arrange
  const untouched: SessionDto = { ...entry, id: 'ses_2', rawText: '', rawDraftRevision: 0, rawLatestRevision: 0 }
  await aRefusedCapture('cap_2', 'rec_2', 120_000)
  await updateCapture('cap_2', { sessionId: 'ses_2' })
  const screen = await openHome([
    recordingAnswer('GET', '/api/v1/recordings/rec_2', { id: 'rec_2', sessionId: 'ses_2', clientRecordingId: 'cap_2', status: 'Receiving', storedSlices: [] }),
    { method: 'DELETE', path: '/api/v1/recordings/rec_2', status: 204 },
    { method: 'GET', path: '/api/v1/sessions/ses_2', body: untouched },
    { method: 'GET', path: '/api/v1/sessions/ses_2/recordings', body: [] },
    { method: 'DELETE', path: '/api/v1/sessions/ses_2', status: 204 },
  ])
  await cardSays(screen, 'The audio is larger than 8 MiB. The audio stays in this browser.')

  // Act
  await userEvent.click(screen.getByRole('button', { name: 'Discard' }))
  await userEvent.click(screen.getByRole('group', { name: 'Discard this audio' }).getByRole('button', { name: 'Discard audio' }))

  // Assert
  await cardSays(screen, 'Tap to record')
  await expect.poll(() => capturesOf(member.identifier)).toEqual([])
  expect((await commands.takeSentRequests()).map(({ method, path }) => `${method} ${path}`)).toEqual(['DELETE /api/v1/recordings/rec_2', 'DELETE /api/v1/sessions/ses_2'])
})

test('the card lets go of the words of an entry that the person deleted, and stops asking for them', async () => {
  // Arrange
  performance.clearResourceTimings()
  await aFinishedCapture()

  // Act
  // The query client of the app tries a failed read again, so a read that the server refuses must not.
  const screen = await openHome([{ method: 'GET', path: '/api/v1/recordings/rec_1', status: 404, body: { status: 404, title: 'Not found' } }], { client: createQueryClient() })

  // Assert
  await cardSays(screen, 'Tap to record')
  await expect.poll(() => capturesOf(member.identifier)).toEqual([])
  await new Promise((resolve) => setTimeout(resolve, 3_500))
  const readsOfTheRecording = performance.getEntriesByType('resource').filter(({ name }) => new URL(name).pathname === '/api/v1/recordings/rec_1')
  expect(readsOfTheRecording).toHaveLength(1)
})

/** The sRGB channels of a CSS color, from the canvas of the browser, as the theme gives colors in oklch. */
function channelsOf(color: string) {
  const context = document.createElement('canvas').getContext('2d')!
  context.fillStyle = color
  context.fillRect(0, 0, 1, 1)
  return [...context.getImageData(0, 0, 1, 1).data.slice(0, 3)]
}

function contrastOf(first: string, second: string) {
  const luminanceOf = (color: string) => {
    const [red, green, blue] = channelsOf(color).map((value) => (value / 255 <= 0.04045 ? value / 255 / 12.92 : ((value / 255 + 0.055) / 1.055) ** 2.4))
    return 0.2126 * red + 0.7152 * green + 0.0722 * blue
  }
  const [light, dark] = [luminanceOf(first), luminanceOf(second)].sort((a, b) => b - a)
  return (light + 0.05) / (dark + 0.05)
}

const recordAndSend: Answer[] = [
  { method: 'POST', path: '/api/v1/sessions', status: 201, body: entry },
  recordingAnswer('POST', '/api/v1/sessions/ses_1/recordings', { status: 'Receiving' }, 201),
  ...Array.from({ length: 20 }, (_, index): Answer => ({ method: 'PUT', path: `/api/v1/recordings/rec_1/slices/${index}`, body: {} })),
  recordingAnswer('POST', '/api/v1/recordings/rec_1/finish', { status: 'Received' }),
  recordingAnswer('GET', '/api/v1/recordings/rec_1', { status: 'Transcribing' }),
]

test('a person on the keyboard starts and stops the recording with the focus on the mic', async () => {
  // Arrange
  await page.viewport(1280, 800)
  micGivesATone()
  const screen = await openHome(recordAndSend)
  await cardSays(screen, 'Tap to record')
  screen.getByRole('button', { name: 'Record' }).element().focus()

  // Act
  await userEvent.keyboard('{Enter}')
  await cardSays(screen, 'Recording')
  await userEvent.keyboard('{Enter}')

  // Assert
  await cardSays(screen, 'Writing the words', 10_000)
  expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Record' }).element())
})

test('in dark mode the stop icon stands out from the red of the button', async () => {
  // Arrange
  document.documentElement.classList.add('dark')
  onTestFinished(() => document.documentElement.classList.remove('dark'))
  micGivesATone()
  const screen = await openHome(recordAndSend)

  // Act
  await userEvent.click(screen.getByRole('button', { name: 'Record' }))

  // Assert
  const stop = screen.getByRole('button', { name: 'Stop recording' })
  await expect.element(stop).toBeVisible()
  const { backgroundColor, color } = getComputedStyle(stop.element())
  expect(contrastOf(backgroundColor, color)).toBeGreaterThanOrEqual(3)
  await userEvent.click(stop)
  await cardSays(screen, 'Writing the words', 10_000)
})

test('a sign-out during a recording stops it and sends all of its audio first, so it asks nothing', { timeout: 30_000 }, async () => {
  // Arrange
  notePageUser(member.identifier)
  micGivesATone()
  const screen = await openHome(recordAndSend)
  await userEvent.click(screen.getByRole('button', { name: 'Record' }))
  await cardSays(screen, 'Recording')
  await expect.poll(async () => (await capturesOf(member.identifier))[0]?.storedSlices ?? 0, { timeout: 5_000 }).toBeGreaterThanOrEqual(2)
  const beforeAQuestion = vi.fn()

  // Act
  await settleUnsentWords(beforeAQuestion)

  // Assert
  expect(beforeAQuestion).not.toHaveBeenCalled()
  const [capture] = await capturesOf(member.identifier)
  expect(capture).toMatchObject({ isEnded: true, isFinished: true, sentSlices: capture.storedSlices })
  expect((await commands.takeSentRequests()).map(({ method, path }) => `${method} ${path}`).pop()).toBe('POST /api/v1/recordings/rec_1/finish')
})

test('a sign-out in another tab stops the recording of this tab first', { timeout: 30_000 }, async () => {
  // Arrange
  notePageUser(member.identifier)
  micGivesATone()
  const screen = await openHome(recordAndSend, { component: HomeInTheLayout })
  await userEvent.click(screen.getByRole('button', { name: 'Record' }))
  await cardSays(screen, 'Recording')
  const tabs = new BroadcastChannel('recall.tabs')
  onTestFinished(() => tabs.close())
  const settled = new Promise((resolve) => {
    tabs.onmessage = ({ data }: MessageEvent<{ kind: string; nonce: string }>) => {
      if (data.kind === 'settled' && data.nonce === 'n1') resolve(data)
    }
  })

  // Act
  tabs.postMessage({ kind: 'settle', nonce: 'n1' })

  // Assert
  await settled
  expect((await capturesOf(member.identifier))[0]).toMatchObject({ isEnded: true })
  await cardSays(screen, 'Writing the words', 10_000)
})

test('a sign-out sends at once the audio that waits for a retry, so it asks nothing', { timeout: 30_000 }, async () => {
  // Arrange
  notePageUser(member.identifier)
  // The longest wait of each retry, so that the third wait is longer than the quiet limit of the sign-out.
  vi.spyOn(Math, 'random').mockReturnValue(0.99)
  await aFinishedCapture({ isFinished: false })
  const screen = await openHome([recordingAnswer('POST', '/api/v1/recordings/rec_1/finish', {}, 503)])
  await expect.poll(async () => (await commands.takeSentRequests()).length, { timeout: 10_000 }).toBeGreaterThan(0)
  await expect.poll(async () => (await commands.takeSentRequests()).length, { timeout: 10_000 }).toBeGreaterThan(0)
  await expect.poll(async () => (await commands.takeSentRequests()).length, { timeout: 10_000 }).toBeGreaterThan(0)
  await commands.answerRequests([...onServer, recordingAnswer('POST', '/api/v1/recordings/rec_1/finish', { status: 'Received' }), recordingAnswer('GET', '/api/v1/recordings/rec_1', { status: 'Received' })])
  // A question here would hold the sign-out, so the test answers it and the sign-out fails.
  const beforeAQuestion = vi.fn(() => setTimeout(() => answerAboutUnsentWords('stay')))

  // Act
  await settleUnsentWords(beforeAQuestion)

  // Assert
  expect(beforeAQuestion).not.toHaveBeenCalled()
  expect(await capturesOf(member.identifier)).toMatchObject([{ isFinished: true }])
  await cardSays(screen, 'Writing the words')
})

// After the tests that record, because the mic state of the tab keeps this stop until the next tap.
test('a deleted entry stops its recording, and the card says why', { timeout: 20_000 }, async () => {
  // Arrange
  micGivesATone()
  const screen = await openHome([
    { method: 'POST', path: '/api/v1/sessions', status: 201, body: entry },
    recordingAnswer('POST', '/api/v1/sessions/ses_1/recordings', { status: 'Receiving' }, 201),
    // The person deleted the entry in another tab, and its Recording went with it.
    { method: 'PUT', path: '/api/v1/recordings/rec_1/slices/0', status: 404, body: { status: 404, title: 'Not found' } },
  ])

  // Act
  await userEvent.click(screen.getByRole('button', { name: 'Record' }))

  // Assert
  await cardSays(screen, 'The entry was deleted', 10_000)
  await cardSays(screen, 'The entry of this recording was deleted, so the recording stopped. Tap to record a new entry.')
  await expect.element(screen.getByRole('button', { name: 'Record' })).toBeEnabled()
  expect(await capturesOf(member.identifier)).toEqual([])
})

test('on plain HTTP the card says that the mic needs HTTPS, and the mic stays off', async () => {
  // Arrange
  vi.spyOn(window, 'isSecureContext', 'get').mockReturnValue(false)

  // Act
  const screen = await openHome([])

  // Assert
  await cardSays(screen, 'The mic needs HTTPS')
  await expect.element(screen.getByRole('button', { name: 'Record' })).toBeDisabled()
  await expect.element(screen.getByRole('link', { name: 'Turn on HTTPS' })).toBeVisible()
})

test('a browser that refuses the audio store says so, and the mic stays off', async () => {
  // Arrange
  vi.spyOn(IDBDatabase.prototype, 'transaction').mockImplementation(() => {
    throw new DOMException('The store is closed.', 'InvalidStateError')
  })

  // Act
  const screen = await openHome([])

  // Assert
  await cardSays(screen, 'This browser does not keep audio')
  await expect.element(screen.getByRole('button', { name: 'Record' })).toBeDisabled()
  await expect.element(screen.getByRole('button', { name: 'Type instead' })).toBeVisible()
})

// Last, because the mic state of the tab keeps the denial until the next tap.
test('a denied mic says how to allow it, and lets the person type instead', async () => {
  // Arrange
  vi.spyOn(navigator.mediaDevices, 'getUserMedia').mockRejectedValue(new DOMException('Permission denied', 'NotAllowedError'))
  const screen = await openHome([])

  // Act
  await userEvent.click(screen.getByRole('button', { name: 'Record' }))

  // Assert
  await cardSays(screen, 'The browser blocks the mic')
  await cardSays(screen, 'set Microphone to Allow, then try again. You can type your entry now.')
  await expect.element(screen.getByRole('button', { name: 'Type instead' })).toBeVisible()
  await expect.element(screen.getByRole('button', { name: 'Try again' })).toBeEnabled()
  expect(await capturesOf(member.identifier)).toEqual([])
})
