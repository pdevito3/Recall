import { useQuery } from '@tanstack/react-query'
import { useEffect, useState } from 'react'

import { Button } from '@/components/ui/button'
import { useJournalZone } from '@/domain/settings/apis/user-setting'

import { sessionQueryOptions } from '../apis/get-session'
import { JournalEditor } from '../editor/journal-editor'
import { SaveStatusLine } from './save-status'
import type { Composer } from './use-composer'

const editorLabel = 'New entry'

export function ComposerForm({ composer }: { composer: Composer }) {
  const zone = useJournalZone()
  return (
    <section aria-label="Write a new entry" className="flex flex-col gap-2">
      <ComposerEditor composer={composer} />
      <div className="flex flex-wrap items-center justify-between gap-x-4 gap-y-2">
        <SaveStatusLine status={composer.status} zone={zone} />
        <div className="ml-auto flex items-center gap-3">
          <p className="text-xs text-muted-foreground max-md:hidden">Type @ to tag a person</p>
          <Button isDisabled={!composer.canFinish} onPress={composer.finish}>
            Done
          </Button>
        </div>
      </div>
    </section>
  )
}

function ComposerEditor({ composer }: { composer: Composer }) {
  const { saver, resumedSessionId, startOver } = composer
  // The words of an earlier page load: first those with no reply, then those that the Server holds. The editor reads them once.
  const [opening] = useState(() => {
    const unsettled = saver?.unsettledText() ?? null
    return { unsettled, needsServerWords: unsettled === null && resumedSessionId !== null }
  })
  const resumed = useQuery({
    ...sessionQueryOptions(resumedSessionId ?? ''),
    enabled: opening.needsServerWords,
  })

  useEffect(() => {
    if (resumed.data) saver?.moveBaseTo(resumed.data.rawLatestRevision)
    // The entry is gone, for example after a delete in another tab, so the page starts a new one.
    if (resumed.isError) startOver()
  }, [resumed.data, resumed.isError, saver, startOver])

  if (opening.needsServerWords && resumed.isPending) {
    return <div role="status" aria-label="Loading your entry" className="min-h-32 rounded-lg border bg-card" />
  }

  return (
    <JournalEditor
      key={composer.editorKey}
      initialText={composer.editorKey === 0 ? (opening.unsettled ?? resumed.data?.rawText ?? '') : ''}
      label={editorLabel}
      placeholder="What is on your mind?"
      autoFocus={composer.autoFocus}
      onChange={composer.handleChange}
      onSubmit={composer.finish}
    />
  )
}
