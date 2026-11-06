import { useNavigate } from '@tanstack/react-router'
import { useEffect, useRef, useState } from 'react'

import type { SessionDto } from '@/api/generated/models/types/session-dto'
import { Button } from '@/components/ui/button'
import { useJournalZone } from '@/domain/settings/apis/user-setting'

import type { DraftChannel } from '../apis/session.keys'
import { shownStatusOf } from '../autosave/draft-saver'
import { useDraftSaver } from '../autosave/use-draft-saver'
import { EditorToolbar } from '../editor/editor-toolbar'
import { focusEditorIn } from '../editor/focus-editor'
import { JournalEditor } from '../editor/journal-editor'
import { ConflictNotice } from './conflict-notice'
import { SaveStatusLine } from './save-status'
import { SessionHistory } from './session-history'

const channelLabels: Record<DraftChannel, string> = { raw: 'Your words', cleaned: 'Cleaned version' }

const draftOf = (session: SessionDto, channel: DraftChannel) =>
  channel === 'raw' ? session.rawText : (session.cleanedText ?? '')
const draftRevisionOf = (session: SessionDto, channel: DraftChannel) =>
  channel === 'raw' ? session.rawDraftRevision : session.cleanedDraftRevision
const latestOf = (session: SessionDto, channel: DraftChannel) =>
  channel === 'raw' ? session.rawLatestRevision : session.cleanedLatestRevision

export function DraftEditor({ session, channel }: { session: SessionDto; channel: DraftChannel }) {
  const zone = useJournalZone()
  const { saver, snapshot } = useDraftSaver({
    channel,
    clientSessionId: session.clientSessionId,
    startedAt: session.startedAt,
    sessionId: session.id,
    baseRevision: draftRevisionOf(session, channel),
    latestRevision: latestOf(session, channel),
  })

  const navigate = useNavigate()
  const [opened, setOpened] = useState(() => ({
    text: saver.wordsToOpen() ?? draftOf(session, channel),
    key: 0,
    takesFocus: snapshot.movedFromDeletedEntry,
  }))
  const [revisionsAtOpen] = useState(() => ({ draft: draftRevisionOf(session, channel), latest: latestOf(session, channel) }))
  const [opensAConflict] = useState(() => saver.hasUnseenConflict())
  const [wasUpdated, setWasUpdated] = useState(false)
  const currentText = useRef(opened.text)
  const section = useRef<HTMLElement>(null)

  const reopenWith = (text: string) => {
    currentText.current = text
    setOpened((previous) => ({ text, key: previous.key + 1, takesFocus: true }))
  }

  const serverText = draftOf(session, channel)
  const serverDraft = draftRevisionOf(session, channel)
  const serverLatest = latestOf(session, channel)
  useEffect(() => {
    if (opensAConflict || saver.unsettledText() !== null) return
    saver.dismissConflict()
    saver.moveBaseTo(revisionsAtOpen.draft, revisionsAtOpen.latest)
  }, [saver, revisionsAtOpen, opensAConflict])
  useEffect(() => {
    const { baseRevision, conflict } = saver.getSnapshot()
    if (serverDraft <= baseRevision || saver.unsettledText() !== null || conflict === 'wordsMovedToHistory') return
    saver.moveBaseTo(serverDraft, serverLatest)
    if (serverText === currentText.current) return
    currentText.current = serverText
    const hadFocus = section.current?.contains(document.activeElement) ?? false
    setOpened((previous) => ({ text: serverText, key: previous.key + 1, takesFocus: hadFocus }))
    setWasUpdated(true)
  }, [saver, serverDraft, serverLatest, serverText])

  const newEntryId = snapshot.movedFromDeletedEntry && snapshot.sessionId !== session.id ? snapshot.sessionId : null
  useEffect(() => {
    if (newEntryId) void navigate({ to: '/sessions/$id', params: { id: newEntryId }, replace: true })
  }, [newEntryId, navigate])

  const showNewest = async () => {
    const newest = await saver.takeNewestWords()
    if (newest !== null) reopenWith(newest)
  }

  const keep = () => {
    saver.keepWords(currentText.current)
    focusEditorIn(section.current)
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
          <ConflictNotice conflict={snapshot.conflict} onShowNewest={() => void showNewest()} onKeep={keep} />
        )}
        {wasUpdated && (
          <div role="status" className="flex flex-wrap items-center justify-between gap-2 rounded-lg border bg-muted p-3 text-sm dark:bg-card">
            <p>These words changed in another tab or device. This page now shows the newest words.</p>
            <Button variant="ghost" onPress={() => setWasUpdated(false)}>Dismiss</Button>
          </div>
        )}
        <JournalEditor
          key={opened.key}
          initialText={opened.text}
          label={channelLabels[channel]}
          autoFocus={opened.takesFocus}
          onChange={(text) => {
            currentText.current = text
            setWasUpdated(false)
            saver.edit(text)
          }}
          onSubmit={() => saver.flush()}
          toolbar={(editor) => <EditorToolbar editor={editor} />}
        />
        <SaveStatusLine status={shownStatusOf(snapshot)} zone={zone} />
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
