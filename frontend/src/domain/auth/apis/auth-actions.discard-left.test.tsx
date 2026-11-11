import { afterEach, expect, test, vi } from 'vitest'
import { render } from 'vitest-browser-react'

import { getRecording } from '@/api/generated/recordings/clients/get-recording'
import { capturesOf, removeCapture, updateCapture } from '@/domain/capture/audio-store'
import { apiClient } from '@/lib/api-client'
import { notePageUser } from '@/lib/page-user'
import { storeAudioThatWaits } from '@/test/audio-that-waits'
import { member } from '@/test/session'

import { UnsentWordsAtLogoutDialog } from '../components/unsent-words-at-logout-dialog'
import { logout } from './auth-actions'
import { leaveTheApp, leftTheApp } from './leave-the-app'
import { LeftTheAppFirst } from './unsent-words-at-logout'

vi.mock('@/api/generated/recordings/clients/get-recording')
vi.mock('@/domain/capture/use-capture', async (original) => ({
  ...(await original<typeof import('@/domain/capture/use-capture')>()),
  stopRecordingOfThisTab: vi.fn(),
}))

/** A replace with only a hash stays on the page of the test, so a test that fails does not take the page away. */
const nowhere = '#session-ended'

afterEach(async () => {
  vi.restoreAllMocks()
  for (const capture of await capturesOf(member.identifier)) await removeCapture(capture.clientRecordingId)
  history.replaceState(null, '', window.location.pathname + window.location.search)
})

test('a way out while a discard ends the audio on the server stops the sign-out, and the audio stays and shows on the screen of the way out', { timeout: 30_000 }, async () => {
  notePageUser(member.identifier)
  // A refusal waits for the person, so the send before the sign-out has nothing to try.
  await storeAudioThatWaits('cap_discarded', { problem: { kind: 'refused', text: 'The audio is larger than 8 MiB.' } })
  await updateCapture('cap_discarded', { recordingId: 'rec_1' })
  let answerRecording!: (value: unknown) => void
  vi.mocked(getRecording).mockReturnValue(new Promise((settle) => (answerRecording = settle)) as never)
  const post = vi.spyOn(apiClient, 'post')
  const screen = await render(<UnsentWordsAtLogoutDialog />)

  const signingOut = logout()
  await screen.getByRole('button', { name: 'Discard audio and sign out' }).click({ timeout: 15_000 })
  await vi.waitFor(() => expect(getRecording).toHaveBeenCalledOnce(), { timeout: 15_000 })
  leaveTheApp({ why: 'sessionEnded', to: nowhere })
  answerRecording({ status: 'Transcribed', storedSlices: [], byteLength: 0 })

  await expect(signingOut).rejects.toBeInstanceOf(LeftTheAppFirst)
  expect(post).not.toHaveBeenCalled()
  await vi.waitFor(() => expect(leftTheApp()).toMatchObject({ why: 'sessionEnded', words: [], audio: [{ clientRecordingId: 'cap_discarded' }] }))
  expect(await capturesOf(member.identifier)).toMatchObject([{ clientRecordingId: 'cap_discarded', storedSlices: 1 }])
  expect(window.location.hash).toBe('')
})
