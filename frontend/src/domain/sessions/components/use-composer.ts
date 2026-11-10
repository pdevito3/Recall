import { isCancelledError, onlineManager, useQuery, useQueryClient, type QueryClient } from '@tanstack/react-query'
import { useCallback, useEffect, useLayoutEffect, useRef, useState, useSyncExternalStore } from 'react'
import { flushSync } from 'react-dom'

import type { SessionDto } from '@/api/generated/models/types/session-dto'
import { Notification } from '@/components/notification'
import { locationCaptureIsOn, locationWaitsForAnAnswer, useUserSetting } from '@/domain/settings/apis/user-setting'
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

function locationOfNewEntry(queryClient: QueryClient) {
  if (!locationCaptureIsOn(queryClient)) {
    if (locationWaitsForAnAnswer(queryClient)) Notification.info('This entry saves no location. A question about location capture waits on Settings, in this tab or in another.')
    return undefined
  }
  const position = captureLocation()
  return async () => {
    const found = await position
    return locationCaptureIsOn(queryClient) ? found : null
  }
}

const idleSnapshot: DraftSnapshot = { status: { kind: 'idle' }, conflict: null, sessionId: null, baseRevision: 0, movedFromDeletedEntry: false, isOnlyInMemory: false, waitsForAnotherWriter: false, editing: 'here', wordsOfTheEditor: null }
const noSubscription = () => () => undefined

const isTheEntryGone = (error: unknown) => isProblemDetailsError(error) && error.status === 404
const isWritingNow = () => document.activeElement?.closest('[contenteditable="true"]') != null

const openingWords = (saver: DraftSaver | null, sessionId: string | null, cached: SessionDto | undefined) => {
  const kept = saver?.wordsToOpen() ?? null
  if (kept !== null) return kept
  if (!sessionId) return ''
  if (saver?.wordsInHold() != null) return null
  return onlineManager.isOnline() ? null : (cached?.rawText ?? null)
}

export function useComposer() {
  const queryClient = useQueryClient()
  const owner = useOwner()
  useUserSetting()

  const [saver, setSaverState] = useState<DraftSaver | null>(() => {
    const pointer = readPointer(owner)
    if (!pointer) return null
    return draftSaverFor({ ...pointer, channel: 'raw', baseRevision: 0, baseText: '' }, queryClient)
  })
  const [resumedSessionId] = useState(() => saver?.getSnapshot().sessionId ?? readPointer(owner)?.sessionId ?? null)
  const [finished, setFinished] = useState<{ saver: DraftSaver; words: string; pointer: ComposerPointer | null } | null>(null)
  const [editor, setEditor] = useState(() => {
    const cached = resumedSessionId ? queryClient.getQueryData<SessionDto>(SessionKeys.detail(resumedSessionId)) : undefined
    return { key: 0, words: openingWords(saver, resumedSessionId, cached), takesFocus: false }
  })
  const [isMergeNoted, setIsMergeNoted] = useState(false)
  const currentWords = useRef(editor.words ?? '')
  const heldAtOpen = useRef(saver?.wordsInHold() ?? null)
  const readsAtOpen = useRef(editor.words === null)
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

  const shown = saver ?? finished?.saver ?? null
  const snapshot = useSyncExternalStore(shown?.subscribe ?? noSubscription, shown?.getSnapshot ?? (() => idleSnapshot))

  const sessionId = saver ? snapshot.sessionId : null

  // Another tab edits this entry, so this tab shows its words with no edit. When this tab takes the entry, the editor opens with the words of the last editor.
  const editing = saver ? snapshot.editing : 'here'
  // A saver takes the words that wait in storage when the lock answers, so until then this tab does not know what to open.
  const isLockAnswered = editing !== 'waiting'

  const isReading = editor.words === null
  useEffect(() => {
    // Words that the saver took at the answer of the lock open in the editor with no read.
    if (!isReading || !resumedSessionId || !isLockAnswered || saver?.wordsToOpen() != null) return
    let isCurrent = true
    // The answer must reach the screen before the next key, so that the key lands in the words of the entry.
    const openWords = (words: string) => {
      const opened = saver?.joinHoldInto(words) ?? words
      currentWords.current = opened
      flushSync(() => setEditor(({ key }) => ({ key: key + 1, words: opened, takesFocus: isWritingNow() })))
      if (saver?.wordsInHold() != null) void saver.joinHoldWithNoPage()
    }
    const open = (session: SessionDto) => {
      if (!isCurrent) return
      saver?.moveBaseTo(session.rawDraftRevision, session.rawText)
      openWords(session.rawText)
    }
    const openWithNoRead = () => {
      if (!isCurrent) return
      const lastSaved = saver?.lastSavedWords() ?? null
      const cached = queryClient.getQueryData<SessionDto>(SessionKeys.detail(resumedSessionId))
      if (lastSaved !== null) openWords(lastSaved)
      else if (cached) open(cached)
    }
    const giveUp = setTimeout(openWithNoRead, readWaitMs)
    const readLimit = setInterval(() => void queryClient.cancelQueries({ queryKey: SessionKeys.detail(resumedSessionId), exact: true }), readLimitMs)
    let readRetry: ReturnType<typeof setTimeout> | undefined
    const read = (): Promise<void> =>
      queryClient.fetchQuery({ ...sessionQueryOptions(resumedSessionId), staleTime: 0 }).then(open, (error: unknown) => {
        if (!isCurrent) return
        if (isCancelledError(error)) return read()
        if (isTheEntryGone(error)) {
          if (saver?.wordsInHold() == null) flushSync(startOver)
          else openWords('')
          return
        }
        openWithNoRead()
        clearTimeout(readRetry)
        readRetry = setTimeout(() => void read(), readRetryMs)
      })
    const stopWatching = onlineManager.subscribe((isOnline) => {
      if (!isOnline) {
        openWithNoRead()
        return
      }
      clearTimeout(readRetry)
      void read()
    })
    if (onlineManager.isOnline()) void read()
    else queueMicrotask(openWithNoRead)
    return () => {
      isCurrent = false
      stopWatching()
      clearTimeout(giveUp)
      clearInterval(readLimit)
      clearTimeout(readRetry)
    }
  }, [isReading, resumedSessionId, isLockAnswered, saver, queryClient, startOver])

  useEffect(() => {
    if (readsAtOpen.current) latestSaver.current?.takeHold()
    else void latestSaver.current?.joinHoldWithNoPage()
    return () => void latestSaver.current?.joinHoldWithNoPage()
  }, [])

  useLayoutEffect(
    () =>
      saver?.subscribeToNewWords((joined, why) => {
        currentWords.current = joined
        flushSync(() => {
          setEditor(({ key }) => ({ key: key + 1, words: joined, takesFocus: isWritingNow() }))
          if (why === 'mergedIntoNewest') setIsMergeNoted(true)
        })
      }),
    [saver],
  )

  const shownSession = useQuery({ ...sessionQueryOptions(sessionId ?? ''), enabled: editing === 'elsewhere' && sessionId !== null })
  const wordsOfTheEditor = snapshot.wordsOfTheEditor ?? shownSession.data?.rawText ?? (editing === 'here' ? null : (editor.words ?? ''))
  const [hasEdited, setHasEdited] = useState(editing === 'here')
  const pressedEditHere = useRef(false)
  const placeAtLastRender = useRef(editing)
  const wasElsewhere = useRef(false)
  useLayoutEffect(() => {
    const before = placeAtLastRender.current
    placeAtLastRender.current = editing
    if (editing === 'elsewhere') wasElsewhere.current = true
    if (editing !== 'here' || before === 'here' || !saver) return
    setHasEdited(true)
    const kept = saver.wordsToOpen()
    // At the first grant with no words of another tab, the editor keeps what it opened.
    if (kept === null && !wasElsewhere.current) return
    wasElsewhere.current = false
    const cached = sessionId ? queryClient.getQueryData<SessionDto>(SessionKeys.detail(sessionId)) : undefined
    const words = kept ?? (resumedSessionId ? null : (cached?.rawText ?? ''))
    if (words !== null) currentWords.current = words
    const takesFocus = pressedEditHere.current
    pressedEditHere.current = false
    setEditor(({ key }) => ({ key: key + 1, words, takesFocus }))
  }, [editing, saver, sessionId, resumedSessionId, queryClient])
  const editHere = useCallback(() => {
    pressedEditHere.current = true
    latestSaver.current?.takeHere()
  }, [])

  const beginEntry = useCallback(
    (text: string) => {
      const pointer = { owner, clientSessionId: newUuid(), startedAt: new Date().toISOString(), sessionId: null, touchedAt: Date.now() }
      const started = draftSaverFor(
        {
          ...pointer,
          channel: 'raw',
          baseRevision: 0,
          baseText: '',
          location: locationOfNewEntry(queryClient),
        },
        queryClient,
        true,
      )
      writePointer(pointer)
      setSaver(started)
      started.edit(text)
    },
    [owner, queryClient, setSaver],
  )

  const handleChange = useCallback(
    (text: string, isEmpty: boolean) => {
      currentWords.current = text
      if (isReading) {
        if (latestSaver.current?.wordsInHold() == null) heldAtOpen.current = null
        const held = heldAtOpen.current
        const typed = isEmpty ? held : held === null ? text : joinWords(held, text)
        if (typed === null) latestSaver.current?.releaseFromOutbox()
        else latestSaver.current?.holdInOutbox(typed)
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
    openEditorWith(finished.saver.newestWords() ?? finished.words)
  }, [needsThePersonAfterDone, finished, setSaver, openEditorWith])

  const canFinish = saver !== null && !isReading && editing === 'here' && snapshot.status.kind !== 'refused' && snapshot.conflict === null
  const finish = useCallback(() => {
    if (!saver || !canFinish) return
    saver.flush()
    setFinished({ saver, words: currentWords.current, pointer: readPointer(owner) })
    writePointer(null)
    setSaver(null)
    openEditorWith('')
  }, [saver, canFinish, owner, setSaver, openEditorWith])

  const keepWords = useCallback(() => {
    void saver?.keepWords(currentWords.current).then((merged) => {
      if (merged !== null && latestSaver.current === saver) openEditorWith(merged)
    })
  }, [saver, openEditorWith])

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
    isMergeNoted,
    dismissMergeNote: () => setIsMergeNoted(false),
    showNewest,
    editing,
    hasEdited,
    wordsOfTheEditor,
    editHere,
  }
}

export type Composer = ReturnType<typeof useComposer>
