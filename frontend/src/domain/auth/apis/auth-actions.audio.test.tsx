import { afterEach, expect, test, vi } from 'vitest'
import { render } from 'vitest-browser-react'

import { capturesOf, removeCapture } from '@/domain/capture/audio-store'
import { apiClient } from '@/lib/api-client'
import { notePageUser } from '@/lib/page-user'
import { storeAudioThatWaits } from '@/test/audio-that-waits'
import { member } from '@/test/session'

import { UnsentWordsAtLogoutDialog } from '../components/unsent-words-at-logout-dialog'
import { logout } from './auth-actions'
import { leftTheApp } from './leave-the-app'

/** A replace with only a hash stays on the page of the test, so a test that fails does not take the page away. */
const nowhere = '#signed-out'

afterEach(async () => {
  vi.restoreAllMocks()
  for (const capture of await capturesOf(member.identifier)) await removeCapture(capture.clientRecordingId)
  history.replaceState(null, '', window.location.pathname + window.location.search)
})

test('the way out of a sign-out names only the audio that the person did not answer about', { timeout: 30_000 }, async () => {
  notePageUser(member.identifier)
  // A refusal waits for the person, so the send before the sign-out has nothing to try.
  await storeAudioThatWaits('cap_answered', { problem: { kind: 'refused', text: 'The audio is larger than 8 MiB.' }, startedAt: new Date(Date.now() - 60_000) })
  let answer!: (value: unknown) => void
  const post = vi.spyOn(apiClient, 'post').mockReturnValue(new Promise((settle) => (answer = settle)) as never)
  const screen = await render(<UnsentWordsAtLogoutDialog />)

  const signingOut = logout()
  await screen.getByRole('button', { name: 'Keep audio and sign out' }).click({ timeout: 15_000 })
  await vi.waitFor(() => expect(post).toHaveBeenCalledOnce(), { timeout: 15_000 })
  // A recording that starts while the Server signs out was in no question.
  await storeAudioThatWaits('cap_after')
  answer({ status: 200, data: { provider_logout_url: nowhere } })
  await signingOut

  await vi.waitFor(() => expect(leftTheApp()).toMatchObject({ why: 'signedOut', words: [], audio: [{ clientRecordingId: 'cap_after' }] }))
  const left = leftTheApp()
  if (left?.why === 'anotherPerson') throw new Error('expected the screen of a way out')
  expect(left?.audio).toHaveLength(1)
  expect(window.location.hash).toBe('')
})
