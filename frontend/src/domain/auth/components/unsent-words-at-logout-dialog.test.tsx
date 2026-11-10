import { afterEach, expect, test, vi } from 'vitest'
import { commands } from 'vitest/browser'
import { render } from 'vitest-browser-react'

import { addCapture, capturesOf, confirmSlice, endCapture, removeCapture, storeSlice, updateCapture, type UploadProblem } from '@/domain/capture/audio-store'
import { unsentWordsOnThisDevice, type UnsentWords } from '@/domain/sessions/autosave/draft-saver'
// The app sends each request through this client.
import '@/lib/api-client'
import { notePageUser } from '@/lib/page-user'
import { member } from '@/test/session'
import { settleUnsentWords } from '../apis/unsent-words-at-logout'
import { UnsentWordsAtLogoutDialog } from './unsent-words-at-logout-dialog'

vi.mock('@/domain/sessions/autosave/draft-saver', async (original) => ({
  ...(await original<typeof import('@/domain/sessions/autosave/draft-saver')>()),
  settleDraftSavers: () => Promise.resolve(false),
  unsentWordsOnThisDevice: vi.fn(),
}))

const doc = (text: string) => JSON.stringify({ type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text }] }] })

const words = (text: string, more: Partial<UnsentWords> = {}): UnsentWords => ({ words: doc(text), isRefused: false, isOnlyInMemory: false, discard: vi.fn(), ...more })

async function dialogFor(unsent: UnsentWords[]) {
  vi.mocked(unsentWordsOnThisDevice).mockReturnValue(unsent)
  const screen = await render(<UnsentWordsAtLogoutDialog />)
  const finished = settleUnsentWords(() => undefined)
  await expect.element(screen.getByRole('alertdialog')).toBeInTheDocument()
  return { screen, finished }
}

afterEach(async () => {
  vi.restoreAllMocks()
  for (const capture of await capturesOf(member.identifier)) await removeCapture(capture.clientRecordingId)
})

/** A recording of 0:03 whose first slice of two is on the Server. This page runs no upload, so nothing sends the rest. */
async function audioThatDidNotGo(problem: UploadProblem | null = null) {
  notePageUser(member.identifier)
  await addCapture({
    clientRecordingId: 'cap_1',
    owner: member.identifier,
    clientSessionId: 'c1',
    startedAt: new Date(Date.now() - 60_000).toISOString(),
    format: 'Webm',
    storedSlices: 0,
    storedBytes: 0,
    sentSlices: 0,
    durationMs: 0,
    isEnded: false,
    sessionId: 'ses_1',
    recordingId: 'rec_1',
    isFinished: false,
    problem: null,
    touchedAt: Date.now(),
  })
  await storeSlice('cap_1', 0, new Uint8Array(1_000).buffer, 1_000)
  await storeSlice('cap_1', 1, new Uint8Array(500).buffer, 3_000)
  // The Server confirmed the first slice, so this browser let it go.
  await confirmSlice('cap_1', 0)
  await endCapture('cap_1')
  await updateCapture('cap_1', { problem })
}

test('audio that did not go is named at a sign-out, and a keep leaves it in this browser', { timeout: 15_000 }, async () => {
  await audioThatDidNotGo()
  vi.mocked(unsentWordsOnThisDevice).mockReturnValue([])
  const screen = await render(<UnsentWordsAtLogoutDialog />)
  const finished = settleUnsentWords(() => undefined)

  await expect.element(screen.getByRole('heading', { name: 'Some of your audio did not reach the server' }), { timeout: 10_000 }).toBeVisible()
  await expect.element(screen.getByText(/0:01 of audio did not reach the server\. If you keep it, it stays in this browser and uploads when you sign in here again\./)).toBeVisible()
  await expect.element(screen.getByRole('list', { name: 'Audio that did not reach the server' }).getByText('A recording of 0:03. The server lacks 0:01 of it.')).toBeVisible()
  await screen.getByRole('button', { name: 'Keep audio and sign out' }).click()

  ;(await finished)()
  expect(await capturesOf(member.identifier)).toMatchObject([{ clientRecordingId: 'cap_1', storedSlices: 2, sentSlices: 1 }])
})

test('a discard at a sign-out ends the part that the server has, and the audio goes from this browser', async () => {
  // A refusal waits for the person, so the send before the sign-out has nothing to try.
  await audioThatDidNotGo({ kind: 'refused', text: 'The audio is larger than 8 MiB.' })
  await commands.answerRequests([
    { method: 'GET', path: '/api/v1/recordings/rec_1', body: { id: 'rec_1', sessionId: 'ses_1', clientRecordingId: 'cap_1', format: 'Webm', engine: 'Local', status: 'Receiving', problem: 'None', problemText: null, storedSlices: [0], byteLength: 1_000, transcribedMs: 0, version: 1 } },
    { method: 'POST', path: '/api/v1/recordings/rec_1/finish', body: { id: 'rec_1', sessionId: 'ses_1', clientRecordingId: 'cap_1', format: 'Webm', engine: 'Local', status: 'Received', problem: 'None', problemText: null, storedSlices: [0], byteLength: 1_000, transcribedMs: 0, version: 2 } },
  ])
  vi.mocked(unsentWordsOnThisDevice).mockReturnValue([])
  const screen = await render(<UnsentWordsAtLogoutDialog />)
  const finished = settleUnsentWords(() => undefined)

  await expect.element(screen.getByRole('list', { name: 'Audio that did not reach the server' }).getByText('The server refused this audio.')).toBeVisible()
  await screen.getByRole('button', { name: 'Discard audio and sign out' }).click()

  ;(await finished)()
  expect(await commands.takeSentRequests()).toEqual([{ method: 'POST', path: '/api/v1/recordings/rec_1/finish', body: { sliceCount: 1, byteLength: 1_000 } }])
  expect(await capturesOf(member.identifier)).toEqual([])
})

test('the dialog names each text, so a discard removes only words that the person saw', async () => {
  const one = words('first words')
  const two = words('words of another tab')
  const { screen, finished } = await dialogFor([one, two])

  const list = screen.getByRole('list', { name: 'Words that did not reach the server' })
  await expect.element(list.getByText('first words')).toBeVisible()
  await expect.element(list.getByText('words of another tab')).toBeVisible()
  await screen.getByRole('button', { name: 'Discard words and sign out' }).click()

  ;(await finished)()
  expect(one.discard).toHaveBeenCalledOnce()
  expect(two.discard).toHaveBeenCalledOnce()
})

test('each text has its own Copy, so a person can take words before a sign-out', async () => {
  const copied: string[] = []
  vi.spyOn(navigator.clipboard, 'writeText').mockImplementation((text) => {
    copied.push(text)
    return Promise.resolve()
  })
  const { screen, finished } = await dialogFor([words('first words'), words('words of another tab')])

  const list = screen.getByRole('list', { name: 'Words that did not reach the server' })
  await list.getByRole('button', { name: 'Copy words' }).nth(1).click()

  await vi.waitFor(() => expect(copied).toEqual(['words of another tab']))
  await screen.getByRole('button', { name: 'Keep words and sign out' }).click()
  ;(await finished)()
})

test('a keep does not remove a word', async () => {
  const one = words('first words')
  const { screen, finished } = await dialogFor([one])

  await screen.getByRole('button', { name: 'Keep words and sign out' }).click()

  ;(await finished)()
  expect(one.discard).not.toHaveBeenCalled()
})

test('a refused text is not said to go to the journal at a sign-in', async () => {
  const { screen, finished } = await dialogFor([words('too many words', { isRefused: true })])

  await expect.element(screen.getByText(/The server refused the words marked below, so they cannot go to your journal as they are\./)).toBeVisible()
  await expect.element(screen.getByText(/They go to your journal when you sign in here again\./)).not.toBeInTheDocument()

  await screen.getByRole('button', { name: 'Keep words and sign out' }).click()
  await finished
})

test('words that only a page holds cannot be kept, and the person can stay', async () => {
  const { screen, finished } = await dialogFor([words('words in memory', { isOnlyInMemory: true })])

  await expect.element(screen.getByRole('button', { name: 'Keep words and sign out' })).not.toBeInTheDocument()
  const stayed = expect(finished).rejects.toThrow()
  await screen.getByRole('button', { name: 'Stay signed in' }).click()

  await stayed
})

test('a list of many long texts stays in its box, and the buttons stay in view', async () => {
  const { screen, finished } = await dialogFor(Array.from({ length: 30 }, (_, index) => words(`words ${index} `.repeat(40))))

  const list = screen.getByRole('list', { name: 'Words that did not reach the server' }).element()
  expect(list.clientHeight).toBeLessThan(200)
  await expect.element(screen.getByRole('button', { name: 'Keep words and sign out' })).toBeInViewport()

  await screen.getByRole('button', { name: 'Keep words and sign out' }).click()
  await finished
})
