import { useQueryClient } from '@tanstack/react-query'
import { useCallback, useEffect, useState, useSyncExternalStore } from 'react'

import { useUserSetting } from '@/domain/settings/apis/user-setting'
import { mayTakeFocusOnThisPage } from '@/lib/first-page'
import { newUuid } from '@/lib/new-uuid'

import { draftSaverFor, shownStatusOf, type DraftSaver, type DraftSnapshot } from '../autosave/draft-saver'
import { useOwner } from '../autosave/use-draft-saver'

const locationWaitMs = 3_000
const resumeWithinMs = 60 * 60 * 1000
const pointerKey = 'recall.composer'

interface ComposerPointer {
  owner: string
  clientSessionId: string
  startedAt: string
  sessionId: string | null
  touchedAt: number
}

function readPointer(owner: string): ComposerPointer | null {
  try {
    const pointer = JSON.parse(sessionStorage.getItem(pointerKey) ?? 'null') as ComposerPointer | null
    return pointer?.owner === owner && Date.now() - pointer.touchedAt < resumeWithinMs ? pointer : null
  } catch {
    return null
  }
}

function writePointer(pointer: ComposerPointer | null) {
  try {
    if (pointer) sessionStorage.setItem(pointerKey, JSON.stringify(pointer))
    else sessionStorage.removeItem(pointerKey)
  } catch {
    return
  }
}

function captureLocation(): Promise<{ latitude: number; longitude: number } | null> {
  return new Promise((resolve) => {
    if (!('geolocation' in navigator)) {
      resolve(null)
      return
    }
    setTimeout(() => resolve(null), locationWaitMs)
    navigator.geolocation.getCurrentPosition(
      ({ coords }) => resolve({ latitude: coords.latitude, longitude: coords.longitude }),
      () => resolve(null),
      { timeout: locationWaitMs, maximumAge: 5 * 60 * 1000 },
    )
  })
}

const idleSnapshot: DraftSnapshot = { status: { kind: 'idle' }, conflict: null, sessionId: null, baseRevision: 0, latestRevision: 0 }
const noSubscription = () => () => undefined

export function useComposer() {
  const queryClient = useQueryClient()
  const owner = useOwner()
  const captureOn = useUserSetting().data?.locationCaptureEnabled ?? false

  const [saver, setSaver] = useState<DraftSaver | null>(() => {
    const pointer = readPointer(owner)
    if (!pointer) return null
    return draftSaverFor({ ...pointer, channel: 'raw', baseRevision: 0, latestRevision: 0 }, queryClient)
  })
  const [resumedSessionId] = useState(() => readPointer(owner)?.sessionId ?? null)
  const [finished, setFinished] = useState<DraftSaver | null>(null)
  const [editorKey, setEditorKey] = useState(0)

  const shown = saver ?? finished
  const snapshot = useSyncExternalStore(shown?.subscribe ?? noSubscription, shown?.getSnapshot ?? (() => idleSnapshot))

  const sessionId = saver ? snapshot.sessionId : null
  useEffect(() => {
    const current = readPointer(owner)
    if (sessionId && current) writePointer({ ...current, sessionId })
  }, [sessionId, owner])

  const handleChange = useCallback(
    (text: string, isEmpty: boolean) => {
      if (saver) {
        saver.edit(text)
        const current = readPointer(owner)
        if (current) writePointer({ ...current, touchedAt: Date.now() })
        return
      }
      if (isEmpty) return
      const pointer = { owner, clientSessionId: newUuid(), startedAt: new Date().toISOString(), sessionId: null, touchedAt: Date.now() }
      const started = draftSaverFor(
        {
          ...pointer,
          channel: 'raw',
          baseRevision: 0,
          latestRevision: 0,
          location: captureOn ? captureLocation() : undefined,
        },
        queryClient,
      )
      writePointer(pointer)
      setSaver(started)
      started.edit(text)
    },
    [saver, owner, captureOn, queryClient],
  )

  const canFinish = saver !== null && snapshot.status.kind !== 'refused'
  const finish = useCallback(() => {
    if (!saver || !canFinish) return
    saver.flush()
    writePointer(null)
    setFinished(saver)
    setSaver(null)
    setEditorKey((key) => key + 1)
  }, [saver, canFinish])

  const startOver = useCallback(() => {
    writePointer(null)
    setSaver(null)
    setEditorKey((key) => key + 1)
  }, [])

  return {
    editorKey,
    autoFocus: mayTakeFocusOnThisPage() || editorKey > 0,
    resumedSessionId,
    saver,
    status: shownStatusOf(snapshot),
    composingSessionId: sessionId,
    canFinish,
    handleChange,
    finish,
    startOver,
  }
}

export type Composer = ReturnType<typeof useComposer>
