import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'

import { Button } from '@/components/ui/button'
import { useJournalZone } from '@/domain/settings/apis/user-setting'

import { sessionQueryOptions } from '../apis/get-session'
import { JournalEditor } from '../editor/journal-editor'
import { SaveStatusLine } from './save-status'
import { forgetComposerEntry, type Composer } from './use-composer'

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
  // The words of an earlier page load: first those with no reply, then those that the Server holds.
  const unsettled = composer.saver?.unsettledText() ?? null
  const needsServerWords = composer.editorKey === 0 && unsettled === null && composer.resumedSessionId !== null
  const resumed = useQuery({
    ...sessionQueryOptions(composer.resumedSessionId ?? ''),
    enabled: needsServerWords,
  })
  const [frozenUnsettled] = useState(unsettled)

  if (needsServerWords && resumed.isPending) {
    return <div role="status" aria-label="Loading your entry" className="min-h-32 rounded-lg border bg-card" />
  }
  if (needsServerWords && resumed.isError) forgetComposerEntry()

  return (
    <JournalEditor
      key={composer.editorKey}
      initialText={composer.editorKey === 0 ? (frozenUnsettled ?? resumed.data?.rawText ?? '') : ''}
      label={editorLabel}
      placeholder="What is on your mind?"
      autoFocus={composer.autoFocus}
      onChange={composer.handleChange}
      onSubmit={composer.finish}
    />
  )
}
