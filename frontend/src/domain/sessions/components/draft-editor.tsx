import { useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef, useState } from 'react'

import type { SessionDto } from '@/api/generated/models/types/session-dto'
import { Button } from '@/components/ui/button'
import { useJournalZone } from '@/domain/settings/apis/user-setting'

import { sessionQueryOptions } from '../apis/get-session'
import type { DraftChannel } from '../apis/session.keys'
import { useDraftSaver } from '../autosave/use-draft-saver'
import { EditorToolbar } from '../editor/editor-toolbar'
import { JournalEditor } from '../editor/journal-editor'
import { ConflictNotice } from './conflict-notice'
import { SaveStatusLine } from './save-status'
import { SessionHistory } from './session-history'

const channelLabels: Record<DraftChannel, string> = { raw: 'Your words', cleaned: 'Cleaned version' }

const draftOf = (session: SessionDto, channel: DraftChannel) =>
  channel === 'raw' ? session.rawText : (session.cleanedText ?? '')
const latestOf = (session: SessionDto, channel: DraftChannel) =>
  channel === 'raw' ? session.rawLatestRevision : session.cleanedLatestRevision

export function DraftEditor({ session, channel }: { session: SessionDto; channel: DraftChannel }) {
  const queryClient = useQueryClient()
  const zone = useJournalZone()
  const { saver, snapshot } = useDraftSaver({
    channel,
    clientSessionId: session.clientSessionId,
    startedAt: session.startedAt,
    sessionId: session.id,
    baseRevision: latestOf(session, channel),
  })

  const [opened, setOpened] = useState(() => ({ text: saver.unsettledText() ?? draftOf(session, channel), key: 0 }))
  const [wasUpdated, setWasUpdated] = useState(false)
  const currentText = useRef(opened.text)

  const reopenWith = (text: string) => {
    currentText.current = text
    setOpened((previous) => ({ text, key: previous.key + 1 }))
  }

  const serverText = draftOf(session, channel)
  const serverLatest = latestOf(session, channel)
  useEffect(() => {
    if (serverLatest <= saver.getSnapshot().baseRevision || saver.unsettledText() !== null) return
    saver.moveBaseTo(serverLatest)
    if (serverText === currentText.current) return
    currentText.current = serverText
    setOpened((previous) => ({ text: serverText, key: previous.key + 1 }))
    setWasUpdated(true)
  }, [saver, serverLatest, serverText])

  const showNewest = async () => {
    const newest = await queryClient.fetchQuery({ ...sessionQueryOptions(session.id), staleTime: 0 })
    saver.moveBaseTo(latestOf(newest, channel))
    saver.dismissConflict()
    reopenWith(draftOf(newest, channel))
  }

  const restore = (text: string) => {
    reopenWith(text)
    saver.edit(text)
    saver.flush()
  }

  return (
    <div className="flex flex-col gap-6">
      <section aria-label={channelLabels[channel]} className="flex flex-col gap-2">
        {snapshot.conflict && (
          <ConflictNotice conflict={snapshot.conflict} onShowNewest={() => void showNewest()} onDismiss={() => saver.dismissConflict()} />
        )}
        {wasUpdated && (
          <div role="status" className="flex flex-wrap items-center justify-between gap-2 rounded-lg border bg-muted p-3 text-sm">
            <p>These words changed in another tab or device. This page now shows the newest words.</p>
            <Button variant="ghost" onPress={() => setWasUpdated(false)}>Dismiss</Button>
          </div>
        )}
        <JournalEditor
          key={opened.key}
          initialText={opened.text}
          label={channelLabels[channel]}
          onChange={(text) => {
            currentText.current = text
            setWasUpdated(false)
            saver.edit(text)
          }}
          onSubmit={() => saver.flush()}
          toolbar={(editor) => <EditorToolbar editor={editor} />}
        />
        <SaveStatusLine status={snapshot.status} zone={zone} />
      </section>
      <SessionHistory
        sessionId={session.id}
        channel={channel}
        currentRevision={channel === 'raw' ? session.rawDraftRevision : session.cleanedDraftRevision}
        onRestore={restore}
      />
    </div>
  )
}
