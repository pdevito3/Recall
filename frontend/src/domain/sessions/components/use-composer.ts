import { isCancelledError, onlineManager, useQueryClient } from '@tanstack/react-query'
import { useCallback, useEffect, useRef, useState, useSyncExternalStore } from 'react'

import type { SessionDto } from '@/api/generated/models/types/session-dto'
import { useUserSetting } from '@/domain/settings/apis/user-setting'
import { mayTakeFocusOnThisPage } from '@/lib/first-page'
import { isProblemDetailsError } from '@/lib/problem-details'
import { newUuid } from '@/lib/new-uuid'

import { sessionQueryOptions } from '../apis/get-session'
import { SessionKeys } from '../apis/session.keys'
import { readPointer, writePointer, type ComposerPointer } from '../autosave/composer-pointer'
import { draftSaverFor, shownStatusOf, type DraftSaver, type DraftSnapshot } from '../autosave/draft-saver'
import { useOwner } from '../autosave/use-draft-saver'
import { joinWords } from '../editor/content'

const locationWaitMs = 3_000
const readWaitMs = 5_000
const readRetryMs = 2_000
const readLimitMs = 30_000

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

const idleSnapshot: DraftSnapshot = { status: { kind: 'idle' }, conflict: null, sessionId: null, baseRevision: 0, latestRevision: 0, movedFromDeletedEntry: false }
const noSubscription = () => () => undefined

const isTheEntryGone = (error: unknown) => isProblemDetailsError(error) && error.status === 404
const isWritingNow = () => document.activeElement?.closest('[contenteditable="true"]') != null

const openingWords = (saver: DraftSaver | null, sessionId: string | null, cached: SessionDto | undefined) => {
  const kept = saver?.wordsToOpen() ?? null
  if (kept !== null) return kept
  if (!sessionId) return ''
  return onlineManager.isOnline() ? null : (cached?.rawText ?? null)
}

export function useComposer() {
  const queryClient = useQueryClient()
  const owner = useOwner()
  const captureOn = useUserSetting().data?.locationCaptureEnabled ?? false

  const [saver, setSaverState] = useState<DraftSaver | null>(() => {
    const pointer = readPointer(owner)
    if (!pointer) return null
    return draftSaverFor({ ...pointer, channel: 'raw', baseRevision: 0, latestRevision: 0 }, queryClient)
  })
  const [resumedSessionId] = useState(() => saver?.getSnapshot().sessionId ?? readPointer(owner)?.sessionId ?? null)
  const [opensAConflict] = useState(() => saver?.hasUnseenConflict() ?? false)
  const [finished, setFinished] = useState<{ saver: DraftSaver; words: string; pointer: ComposerPointer | null } | null>(null)
  const [editor, setEditor] = useState(() => {
    const cached = resumedSessionId ? queryClient.getQueryData<SessionDto>(SessionKeys.detail(resumedSessionId)) : undefined
    return { key: 0, words: openingWords(saver, resumedSessionId, cached), takesFocus: false }
  })
  const currentWords = useRef(editor.words ?? '')
  const typedWhileReading = useRef<string | null>(null)
  const latestSaver = useRef(saver)

  // A second keystroke can come before React renders the first, and it must reach the same entry.
  const setSaver = useCallback((next: DraftSaver | null) => {
    latestSaver.current = next
    setSaverState(next)
  }, [])

  const openEditorWith = useCallback((words: string) => {
    currentWords.current = words
    setEditor(({ key }) => ({ key: key + 1, words, takesFocus: true }))
  }, [])

  const startOver = useCallback(() => {
    writePointer(null)
    setSaver(null)
    openEditorWith('')
  }, [setSaver, openEditorWith])

  const isReading = editor.words === null
  useEffect(() => {
    if (!isReading || !resumedSessionId) return
    let isCurrent = true
    const openWords = (words: string) => {
      if (!opensAConflict) saver?.dismissConflict()
      const typed = typedWhileReading.current
      typedWhileReading.current = null
      const opened = typed === null ? words : joinWords(words, typed)
      currentWords.current = opened
      setEditor(({ key }) => ({ key: key + 1, words: opened, takesFocus: isWritingNow() }))
      if (typed !== null) saver?.edit(opened)
    }
    const open = (session: SessionDto) => {
      if (!isCurrent) return
      saver?.moveBaseTo(session.rawDraftRevision, session.rawLatestRevision)
      openWords(session.rawText)
    }
    const openWithNoRead = () => {
      if (!isCurrent) return
      const lastSaved = saver?.lastSavedWords() ?? null
      const cached = queryClient.getQueryData<SessionDto>(SessionKeys.detail(resumedSessionId))
      if (lastSaved !== null) openWords(lastSaved)
      else if (cached) open(cached)
    }
    if (!onlineManager.isOnline()) {
      openWithNoRead()
      return
    }
    const stopWatching = onlineManager.subscribe((isOnline) => {
      if (!isOnline) openWithNoRead()
    })
    const giveUp = setTimeout(openWithNoRead, readWaitMs)
    const readLimit = setInterval(() => void queryClient.cancelQueries({ queryKey: SessionKeys.detail(resumedSessionId), exact: true }), readLimitMs)
    let readRetry: ReturnType<typeof setTimeout> | undefined
    const read = (): Promise<void> =>
      queryClient.fetchQuery({ ...sessionQueryOptions(resumedSessionId), staleTime: 0 }).then(open, (error: unknown) => {
        if (!isCurrent) return
        if (isCancelledError(error)) return read()
        if (isTheEntryGone(error)) {
          if (typedWhileReading.current === null) startOver()
          else openWords('')
          return
        }
        openWithNoRead()
        readRetry = setTimeout(() => void read(), readRetryMs)
      })
    void read()
    return () => {
      isCurrent = false
      stopWatching()
      clearTimeout(giveUp)
      clearInterval(readLimit)
      clearTimeout(readRetry)
    }
  }, [isReading, resumedSessionId, saver, opensAConflict, queryClient, startOver])

  const shown = saver ?? finished?.saver ?? null
  const snapshot = useSyncExternalStore(shown?.subscribe ?? noSubscription, shown?.getSnapshot ?? (() => idleSnapshot))

  const sessionId = saver ? snapshot.sessionId : null

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
    [owner, captureOn, queryClient, setSaver],
  )

  const handleChange = useCallback(
    (text: string, isEmpty: boolean) => {
      currentWords.current = text
      if (isReading) {
        typedWhileReading.current = isEmpty ? null : text
        if (isEmpty) latestSaver.current?.releaseFromOutbox()
        else latestSaver.current?.holdInOutbox(text)
        return
      }
      const composing = latestSaver.current
      if (composing) {
        composing.edit(text)
        const current = readPointer(owner)
        if (current) writePointer({ ...current, touchedAt: Date.now() })
        return
      }
      if (!isEmpty) beginEntry(text)
    },
    [owner, beginEntry, isReading],
  )

  const needsThePersonAfterDone = saver === null && finished !== null && (snapshot.status.kind === 'refused' || snapshot.conflict !== null)
  useEffect(() => {
    if (!needsThePersonAfterDone) return
    writePointer(finished.pointer && { ...finished.pointer, sessionId: finished.saver.getSnapshot().sessionId })
    setSaver(finished.saver)
    setFinished(null)
    openEditorWith(finished.words)
  }, [needsThePersonAfterDone, finished, setSaver, openEditorWith])

  const canFinish = saver !== null && !isReading && snapshot.status.kind !== 'refused'
  const finish = useCallback(() => {
    if (!saver || !canFinish) return
    saver.flush()
    saver.dismissConflict()
    setFinished({ saver, words: currentWords.current, pointer: readPointer(owner) })
    writePointer(null)
    setSaver(null)
    openEditorWith('')
  }, [saver, canFinish, owner, setSaver, openEditorWith])

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
    autoFocus: mayTakeFocusOnThisPage() || editor.takesFocus,
    status: shownStatusOf(snapshot),
    conflict: saver && !isReading ? snapshot.conflict : null,
    composingSessionId: sessionId,
    canFinish,
    handleChange,
    finish,
    keepWords,
    showNewest,
  }
}

export type Composer = ReturnType<typeof useComposer>
