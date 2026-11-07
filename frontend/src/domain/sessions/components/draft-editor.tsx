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
import { JournalEditor } from '../editor/journal-editor'
import { ConflictNotice, MergedNotice } from './conflict-notice'
import { SaveStatusLine } from './save-status'
import { SessionHistory } from './session-history'

const channelLabels: Record<DraftChannel, string> = { raw: 'Your words', cleaned: 'Cleaned version' }

const draftOf = (session: SessionDto, channel: DraftChannel) =>
  channel === 'raw' ? session.rawText : (session.cleanedText ?? '')
const draftRevisionOf = (session: SessionDto, channel: DraftChannel) =>
  channel === 'raw' ? session.rawDraftRevision : session.cleanedDraftRevision

const changedElsewhereNote = 'These words changed in another tab or device. This page now shows the newest words.'
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
    setChangeNote(changedElsewhereNote)
  }, [saver, serverDraft, serverText])

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

  return (
    <div className="flex flex-col gap-6">
      <section ref={section} aria-label={channelLabels[channel]} className="flex flex-col gap-2">
        {snapshot.conflict && (
          <ConflictNotice conflict={snapshot.conflict} onShowNewest={() => void showNewest()} onKeep={() => void keep()} />
        )}
        {isMergeNoted && <MergedNotice onDismiss={() => setIsMergeNoted(false)} />}
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
        />
        <div className="sticky bottom-0 z-10 bg-background py-2">
          <SaveStatusLine status={shownStatusOf(snapshot)} zone={zone} />
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
