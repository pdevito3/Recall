import { afterEach, expect, test, vi } from 'vitest'

import { capturesOf, removeCapture } from '@/domain/capture/audio-store'
import { stopRecordingOfThisTab } from '@/domain/capture/use-capture'
import { notePageUser } from '@/lib/page-user'
import { storeAudioThatWaits } from '@/test/audio-that-waits'
import { member } from '@/test/session'

import { leaveTheApp, leftTheApp } from './leave-the-app'

vi.mock('@/domain/capture/use-capture', async (original) => ({
  ...(await original<typeof import('@/domain/capture/use-capture')>()),
  stopRecordingOfThisTab: vi.fn(),
}))

/** A replace with only a hash stays on the page of the test, so a test that fails does not take the page away. */
const nowhere = '#left-the-app'

afterEach(async () => {
  vi.restoreAllMocks()
  for (const capture of await capturesOf(member.identifier)) await removeCapture(capture.clientRecordingId)
  history.replaceState(null, '', window.location.pathname + window.location.search)
})

test('a session that ends while audio waits stops the recording of this tab, names all the audio, keeps it in this browser, and leaves only at Leave', async () => {
  notePageUser(member.identifier)
  await storeAudioThatWaits('cap_waits', { startedAt: new Date(Date.now() - 60_000) })
  // The recorder stores its last part as it stops.
  vi.mocked(stopRecordingOfThisTab).mockImplementation(() => storeAudioThatWaits('cap_recording'))

  leaveTheApp({ why: 'sessionEnded', to: nowhere })

  await vi.waitFor(() => expect(leftTheApp()).toMatchObject({ why: 'sessionEnded', words: [], audio: [{ clientRecordingId: 'cap_waits' }, { clientRecordingId: 'cap_recording' }] }))
  expect(stopRecordingOfThisTab).toHaveBeenCalledOnce()
  expect(document.documentElement.style.opacity).toBe('')
  expect(window.location.hash).toBe('')
  expect(await capturesOf(member.identifier)).toMatchObject([
    { clientRecordingId: 'cap_waits', storedSlices: 1, isFinished: false },
    { clientRecordingId: 'cap_recording', storedSlices: 1, isFinished: false },
  ])
  const left = leftTheApp()
  if (left?.why === 'anotherPerson') throw new Error('expected the screen of a way out')
  left?.leave()
  expect(window.location.hash).toBe(nowhere)
})
