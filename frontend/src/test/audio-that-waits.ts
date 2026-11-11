import { addCapture, endCapture, storeSlice, updateCapture, type UploadProblem } from '@/domain/capture/audio-store'

import { member } from './session'

/** A recording of 0:02 that this browser holds and the Server lacks. */
export async function storeAudioThatWaits(clientRecordingId: string, { problem = { kind: 'offline' }, startedAt = new Date() }: { problem?: UploadProblem; startedAt?: Date } = {}) {
  await addCapture({
    clientRecordingId,
    owner: member.identifier,
    clientSessionId: `client-${clientRecordingId}`,
    startedAt: startedAt.toISOString(),
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
  })
  await storeSlice(clientRecordingId, 0, new Uint8Array(1_000).buffer, 2_000)
  await endCapture(clientRecordingId)
  await updateCapture(clientRecordingId, { problem })
}
