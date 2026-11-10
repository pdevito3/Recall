import { onlineManager } from '@tanstack/react-query'
import { useEffect, useState, useSyncExternalStore } from 'react'

import { useOwner } from '@/domain/sessions/autosave/use-draft-saver'

import { isNotFound, useRecording, useRetryRecording } from './apis/get-recording'
import { CaptureGoneError, capturesOf, removeCapture, subscribeToAudioStore, type StoredCapture } from './audio-store'
import { captureViewOf, followedCapture, micReducer, waitingBesides, type MicEvent, type MicState } from './capture-view'
import { MicStartError, recordingFormat, startRecording, type ActiveRecording } from './mic-recorder'
import { discardStoredAudio, tryAgainToSend } from './upload-queue'

// The mic of this tab outlives the card, so a recording goes on when the person opens another page and shows again on return.
let mic: MicState = { kind: 'idle' }
let active: ActiveRecording | null = null
const micListeners = new Set<() => void>()
/** The captures that this page load recorded, so that its card follows them and not those of another tab. */
const recordedHere = new Set<string>()

function dispatch(event: MicEvent) {
  const next = micReducer(mic, event)
  if (next === mic) return
  mic = next
  micListeners.forEach((listener) => listener())
}

const subscribeToMic = (listener: () => void) => {
  micListeners.add(listener)
  return () => micListeners.delete(listener)
}

async function record(owner: string) {
  dispatch({ type: 'tap' })
  try {
    const recording = await startRecording(owner, (error) => dispatch({ type: error instanceof CaptureGoneError ? 'entryDeleted' : 'storeFailed' }))
    active = recording
    recordedHere.add(recording.clientRecordingId)
    dispatch({ type: 'started', startedAt: recording.startedAt })
    await recording.ended
    if (active === recording) active = null
    dispatch({ type: 'ended' })
  } catch (error) {
    dispatch({ type: 'startFailed', failure: error instanceof MicStartError ? error.failure : 'micFailed' })
  }
}

/** A sign-out stops the recording of this tab, so its last slice is stored and its end is known before the send. */
export async function stopRecordingOfThisTab(): Promise<void> {
  await active?.stop()
}

function useStoredCaptures(owner: string) {
  const [read, setRead] = useState<{ owner: string; captures: StoredCapture[]; canStore: boolean; at: number } | null>(null)
  useEffect(() => {
    if (!owner) return
    let isCurrent = true
    const readAgain = () =>
      void capturesOf(owner).then(
        (captures) => isCurrent && setRead({ owner, captures, canStore: true, at: Date.now() }),
        () => isCurrent && setRead({ owner, captures: [], canStore: false, at: Date.now() }),
      )
    readAgain()
    const stop = subscribeToAudioStore(readAgain)
    return () => {
      isCurrent = false
      stop()
    }
  }, [owner])
  return read?.owner === owner ? read : null
}

function useMicPermission() {
  useEffect(() => {
    let status: PermissionStatus | null = null
    const follow = () => dispatch(status?.state === 'denied' ? { type: 'permissionDenied' } : { type: 'permissionAllowed' })
    // Safari before 16 has no permission query for the mic, and the first tap then tells.
    navigator.permissions
      ?.query({ name: 'microphone' as PermissionName })
      .then((answer) => {
        status = answer
        follow()
        answer.addEventListener('change', follow)
      })
      .catch(() => undefined)
    return () => status?.removeEventListener('change', follow)
  }, [])
}

export function useCapture() {
  const owner = useOwner()
  const micState = useSyncExternalStore(subscribeToMic, () => mic)
  const isOnline = useSyncExternalStore(onlineManager.subscribe.bind(onlineManager), () => onlineManager.isOnline())
  const stored = useStoredCaptures(owner)
  const latest = stored ? followedCapture(stored.captures, recordedHere) : null
  const recording = useRecording(latest?.isFinished ? latest.recordingId : null)
  const retry = useRetryRecording()
  useMicPermission()

  // The person deleted the entry of these words, so the card has nothing left to follow.
  const isEntryGone = isNotFound(recording.error)
  useEffect(() => {
    if (isEntryGone && latest) void removeCapture(latest.clientRecordingId).catch(() => undefined)
  }, [isEntryGone, latest])

  const view = captureViewOf({
    isSecure: window.isSecureContext,
    canRecord: recordingFormat() !== null,
    canStore: stored?.canStore ?? true,
    mic: micState,
    latest: isEntryGone ? null : latest,
    recording: recording.data,
    isOnline,
    now: stored?.at ?? 0,
  })

  return {
    view,
    isLoaded: stored !== null,
    waiting: stored ? waitingBesides(stored.captures, latest) : [],
    level: () => active?.level() ?? 0,
    record: () => void record(owner),
    stop: () => void active?.stop(),
    retry: (recordingId: string) => retry.mutate(recordingId),
    isRetrying: retry.isPending,
    retryError: retry.error,
    sendAgain: (clientRecordingId: string) => void tryAgainToSend(clientRecordingId),
    discard: (clientRecordingId: string) => void discardStoredAudio(stored?.captures.filter((one) => one.clientRecordingId === clientRecordingId) ?? []),
  }
}

export type Capture = ReturnType<typeof useCapture>
