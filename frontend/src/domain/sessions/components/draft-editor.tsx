import { useNavigate } from '@tanstack/react-router'
import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import { flushSync } from 'react-dom'

import type { SessionDto } from '@/api/generated/models/types/session-dto'
import { Button } from '@/components/ui/button'
import { useJournalZone } from '@/domain/settings/apis/user-setting'

import type { DraftChannel } from '../apis/session.keys'
import { shownStatusOf, type WhyNewWords } from '../autosave/draft-saver'
import { useDraftSaver } from '../autosave/use-draft-saver'
import { EditorToolbar } from '../editor/editor-toolbar'
import { focusEditorIn } from '../editor/focus-editor'
import { JournalEditor, JournalText } from '../editor/journal-editor'
import { ConflictNotice, EditedElsewhereNotice, MergedNotice } from './conflict-notice'
import { useFocusEditHereWhenTheEditorLeaves } from './use-focus-edit-here'
import { SaveStatusLine } from './save-status'
import { SessionHistory } from './session-history'

const channelLabels: Record<DraftChannel, string> = { raw: 'Your words', cleaned: 'Cleaned version' }

const draftOf = (session: SessionDto, channel: DraftChannel) =>
  channel === 'raw' ? session.rawText : (session.cleanedText ?? '')
const draftRevisionOf = (session: SessionDto, channel: DraftChannel) =>
  channel === 'raw' ? session.rawDraftRevision : session.cleanedDraftRevision

const changedElsewhereNote = 'These words changed in another tab or device. This page now shows the newest words.'
// A Cleanup on the server also writes Cleaned, so the note of Cleaned names it.
const notesOfServerChange: Record<DraftChannel, string> = {
  raw: changedElsewhereNote,
  cleaned: 'Cleaned changed in a Cleanup, another tab, or another device. This page now shows the newest words.',
}
const notesOfNewWords: Record<Exclude<WhyNewWords, 'mergedIntoNewest'>, string> = {
  heldWordsJoined: 'Words that you typed earlier and that were not saved yet are now on this page.',
  draftHoldsMore: changedElsewhereNote,
}

export function DraftEditor({ session, channel }: { session: SessionDto; channel: DraftChannel }) {
  const zone = useJournalZone()
  const { saver, snapshot } = useDraftSaver({
    channel,
    clientSessionId: session.clientSessionId,
    startedAt: session.startedAt,
    sessionId: session.id,
    baseRevision: draftRevisionOf(session, channel),
    baseText: draftOf(session, channel),
  })

  const navigate = useNavigate()
  const [opened, setOpened] = useState(() => ({
    text: saver.wordsToOpen() ?? draftOf(session, channel),
    key: 0,
    takesFocus: snapshot.movedFromDeletedEntry,
  }))
  const [draftRevisionAtOpen] = useState(() => draftRevisionOf(session, channel))
  const [textAtOpen] = useState(() => draftOf(session, channel))
  const [changeNote, setChangeNote] = useState<string | null>(null)
  const [isMergeNoted, setIsMergeNoted] = useState(false)
  const currentText = useRef(opened.text)
  const section = useRef<HTMLElement>(null)
  const bar = useRef<HTMLDivElement>(null)
  const editHereRef = useFocusEditHereWhenTheEditorLeaves(snapshot.editing)

  const reopenWith = (text: string) => {
    currentText.current = text
    setOpened((previous) => ({ text, key: previous.key + 1, takesFocus: true }))
  }

  const serverText = draftOf(session, channel)
  const serverDraft = draftRevisionOf(session, channel)
  useEffect(() => {
    if (saver.hasConflict() || saver.unsettledText() !== null) return
    saver.moveBaseTo(draftRevisionAtOpen, textAtOpen)
  }, [saver, draftRevisionAtOpen, textAtOpen])
  useEffect(() => {
    if (serverDraft <= saver.getSnapshot().baseRevision || saver.unsettledText() !== null || saver.hasConflict()) return
    saver.moveBaseTo(serverDraft, serverText)
    if (serverText === currentText.current) return
    currentText.current = serverText
    const hadFocus = section.current?.contains(document.activeElement) ?? false
    setOpened((previous) => ({ text: serverText, key: previous.key + 1, takesFocus: hadFocus }))
    setChangeNote(notesOfServerChange[channel])
  }, [saver, serverDraft, serverText, channel])

  useLayoutEffect(
    () =>
      saver.subscribeToNewWords((joined, why) => {
        currentText.current = joined
        const hadFocus = section.current?.contains(document.activeElement) ?? false
        flushSync(() => {
          setOpened((previous) => ({ text: joined, key: previous.key + 1, takesFocus: hadFocus }))
          if (why === 'mergedIntoNewest') setIsMergeNoted(true)
          else setChangeNote(notesOfNewWords[why])
        })
      }),
    [saver],
  )

  // The editor opens when this tab edits, with the words of the tab that edited before.
  const isEditingHere = snapshot.editing === 'here'
  const [hasEdited, setHasEdited] = useState(isEditingHere)
  const pressedEditHere = useRef(false)
  const wasElsewhere = useRef(false)
  const placeAtLastRender = useRef(snapshot.editing)
  useLayoutEffect(() => {
    const before = placeAtLastRender.current
    placeAtLastRender.current = snapshot.editing
    if (snapshot.editing === 'elsewhere') wasElsewhere.current = true
    if (snapshot.editing !== 'here' || before === 'here') return
    setHasEdited(true)
    const kept = saver.wordsToOpen()
    // With no unsent words, the editor and the base of the next save hold the newest of the read of the take and the cache of the page.
    if (kept === null && !saver.hasConflict() && saver.getSnapshot().baseRevision < serverDraft) saver.moveBaseTo(serverDraft, serverText)
    const text = kept ?? saver.wordsOfTheBase() ?? serverText
    // At the first grant, an editor that already shows these words keeps its caret.
    if (!wasElsewhere.current && text === currentText.current) return
    wasElsewhere.current = false
    currentText.current = text
    setChangeNote(null)
    const takesFocus = pressedEditHere.current
    pressedEditHere.current = false
    setOpened((previous) => ({ text, key: previous.key + 1, takesFocus }))
  }, [saver, snapshot.editing, serverText, serverDraft])

  const editHere = () => {
    pressedEditHere.current = true
    saver.takeHere()
  }

  const newEntryId = snapshot.movedFromDeletedEntry && snapshot.sessionId !== session.id ? snapshot.sessionId : null
  useEffect(() => {
    if (newEntryId) void navigate({ to: '/sessions/$id', params: { id: newEntryId }, replace: true })
  }, [newEntryId, navigate])

  const showNewest = async () => {
    const newest = await saver.takeNewestWords()
    if (newest !== null) reopenWith(newest)
  }

  const keep = async () => {
    const merged = await saver.keepWords(currentText.current)
    if (merged !== null) reopenWith(merged)
    else focusEditorIn(section.current)
  }

  const restore = (text: string) => {
    reopenWith(text)
    saver.edit(text)
    saver.flush()
  }

  const status = shownStatusOf(snapshot)
  if (!isEditingHere) {
    const words = snapshot.wordsOfTheEditor ?? serverText
    return (
      <div className="flex flex-col gap-6">
        <section aria-label={channelLabels[channel]} className="flex flex-col gap-2">
          {snapshot.editing === 'elsewhere' && <EditedElsewhereNotice hasLost={hasEdited} onEditHere={editHere} editHereRef={editHereRef} />}
          <div className="rounded-lg border p-3">
            <JournalText key={words} text={words} label={`${channelLabels[channel]}, open in another tab`} />
          </div>
        </section>
        <SessionHistory sessionId={session.id} channel={channel} currentRevision={serverDraft} />
      </div>
    )
  }
  return (
    <div className="flex flex-col gap-6">
      <section ref={section} aria-label={channelLabels[channel]} className="flex flex-col gap-2">
        {snapshot.conflict && (
          <ConflictNotice conflict={snapshot.conflict} onShowNewest={() => void showNewest()} onKeep={() => void keep()} />
        )}
        {/* While the merged words wait to save, they are not in the newest words yet. */}
        {isMergeNoted && status.kind !== 'waitingForAnotherWriter' && <MergedNotice onDismiss={() => setIsMergeNoted(false)} />}
        {changeNote && (
          <div role="status" className="flex flex-wrap items-center justify-between gap-2 rounded-lg border bg-muted p-3 text-sm dark:bg-card">
            <p>{changeNote}</p>
            <Button variant="ghost" onPress={() => setChangeNote(null)}>Dismiss</Button>
          </div>
        )}
        <JournalEditor
          revision={opened.key}
          initialText={opened.text}
          label={channelLabels[channel]}
          autoFocus={opened.takesFocus}
          onChange={(text) => {
            currentText.current = text
            setChangeNote(null)
            saver.edit(text)
          }}
          onSubmit={() => saver.flush()}
          toolbar={(editor) => <EditorToolbar editor={editor} />}
          barRef={bar}
        />
        <div ref={bar} className="sticky bottom-0 z-10 bg-background py-2">
          <SaveStatusLine status={status} zone={zone} />
        </div>
      </section>
      <SessionHistory
        sessionId={session.id}
        channel={channel}
        currentRevision={serverDraft}
        onRestore={restore}
      />
    </div>
  )
}
