import { onlineManager, useQueryClient } from '@tanstack/react-query'
import { useCallback, useEffect, useRef, useState, useSyncExternalStore } from 'react'

import type { SessionDto } from '@/api/generated/models/types/session-dto'
import { useUserSetting } from '@/domain/settings/apis/user-setting'
import { mayTakeFocusOnThisPage } from '@/lib/first-page'
import { newUuid } from '@/lib/new-uuid'

import { sessionQueryOptions } from '../apis/get-session'
import { SessionKeys } from '../apis/session.keys'
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

const openingWords = (saver: DraftSaver | null, sessionId: string | null, cached: SessionDto | undefined) => {
  const unsettled = saver?.unsettledText() ?? null
  if (unsettled !== null) return unsettled
  if (!sessionId) return ''
  return onlineManager.isOnline() ? null : (cached?.rawText ?? null)
}

export function useComposer() {
  const queryClient = useQueryClient()
  const owner = useOwner()
  const captureOn = useUserSetting().data?.locationCaptureEnabled ?? false

  const [saver, setSaver] = useState<DraftSaver | null>(() => {
    const pointer = readPointer(owner)
    if (!pointer) return null
    return draftSaverFor({ ...pointer, channel: 'raw', baseRevision: 0, latestRevision: 0 }, queryClient)
  })
  const [resumedSessionId] = useState(() => saver?.getSnapshot().sessionId ?? readPointer(owner)?.sessionId ?? null)
  const [finished, setFinished] = useState<DraftSaver | null>(null)
  const [editor, setEditor] = useState(() => {
    const cached = resumedSessionId ? queryClient.getQueryData<SessionDto>(SessionKeys.detail(resumedSessionId)) : undefined
    return { key: 0, words: openingWords(saver, resumedSessionId, cached) }
  })
  const currentWords = useRef(editor.words ?? '')
  const latestSaver = useRef(saver)
  useEffect(() => {
    latestSaver.current = saver
  }, [saver])

  const openEditorWith = useCallback((words: string) => {
    currentWords.current = words
    setEditor(({ key }) => ({ key: key + 1, words }))
  }, [])

  const startOver = useCallback(() => {
    writePointer(null)
    setSaver(null)
    openEditorWith('')
  }, [openEditorWith])

  const isReading = editor.words === null
  useEffect(() => {
    if (!isReading || !resumedSessionId) return
    const open = (session: SessionDto) => {
      saver?.moveBaseTo(session.rawDraftRevision, session.rawLatestRevision)
      saver?.dismissConflict()
      currentWords.current = session.rawText
      setEditor(({ key }) => ({ key, words: session.rawText }))
    }
    void queryClient.fetchQuery({ ...sessionQueryOptions(resumedSessionId), staleTime: 0 }).then(open, () => {
      const cached = queryClient.getQueryData<SessionDto>(SessionKeys.detail(resumedSessionId))
      if (cached) open(cached)
      else startOver()
    })
  }, [isReading, resumedSessionId, saver, queryClient, startOver])

  const shown = saver ?? finished
  const snapshot = useSyncExternalStore(shown?.subscribe ?? noSubscription, shown?.getSnapshot ?? (() => idleSnapshot))

  const sessionId = saver ? snapshot.sessionId : null
  useEffect(() => {
    const current = readPointer(owner)
    if (sessionId && current) writePointer({ ...current, sessionId })
  }, [sessionId, owner])

  const beginEntry = useCallback(
    (text: string) => {
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
    [owner, captureOn, queryClient],
  )

  const handleChange = useCallback(
    (text: string, isEmpty: boolean) => {
      currentWords.current = text
      if (saver) {
        saver.edit(text)
        const current = readPointer(owner)
        if (current) writePointer({ ...current, touchedAt: Date.now() })
        return
      }
      if (!isEmpty) beginEntry(text)
    },
    [saver, owner, beginEntry],
  )

  const isEntryGone = saver !== null && snapshot.status.kind === 'refused' && snapshot.status.isGone
  useEffect(() => {
    if (!isEntryGone) return
    saver.discard()
    beginEntry(currentWords.current)
  }, [isEntryGone, saver, beginEntry])

  const canFinish = saver !== null && !isReading && snapshot.status.kind !== 'refused'
  const finish = useCallback(() => {
    if (!saver || !canFinish) return
    saver.flush()
    writePointer(null)
    setFinished(saver)
    setSaver(null)
    openEditorWith('')
  }, [saver, canFinish, openEditorWith])

  const keepWords = useCallback(() => {
    saver?.keepWords(currentWords.current)
  }, [saver])

  const showNewest = useCallback(() => {
    void saver?.takeNewestWords().then((newest) => {
      if (newest !== null && latestSaver.current === saver) openEditorWith(newest)
    })
  }, [saver, openEditorWith])

  return {
    editorKey: editor.key,
    words: editor.words,
    autoFocus: mayTakeFocusOnThisPage() || editor.key > 0,
    status: shownStatusOf(snapshot),
    conflict: saver ? snapshot.conflict : null,
    composingSessionId: sessionId,
    canFinish,
    handleChange,
    finish,
    keepWords,
    showNewest,
  }
}

export type Composer = ReturnType<typeof useComposer>
