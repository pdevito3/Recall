import { useRef } from 'react'

import { Button } from '@/components/ui/button'
import { useJournalZone } from '@/domain/settings/apis/user-setting'

import { focusEditorIn } from '../editor/focus-editor'
import { JournalEditor } from '../editor/journal-editor'
import { ConflictNotice } from './conflict-notice'
import { SaveStatusLine } from './save-status'
import type { Composer } from './use-composer'

export function ComposerForm({ composer }: { composer: Composer }) {
  const zone = useJournalZone()
  const section = useRef<HTMLElement>(null)
  return (
    <section ref={section} aria-label="Write a new entry" className="flex flex-col gap-2">
      {composer.conflict && (
        <ConflictNotice
          conflict={composer.conflict}
          onShowNewest={composer.showNewest}
          onKeep={() => {
            composer.keepWords()
            focusEditorIn(section.current)
          }}
        />
      )}
      <JournalEditor
        key={composer.editorKey}
        initialText={composer.words ?? ''}
        label="New entry"
        placeholder={composer.words === null ? '' : 'What is on your mind?'}
        autoFocus={composer.autoFocus}
        onChange={composer.handleChange}
        onSubmit={composer.finish}
      />
      {composer.words === null && (
        <p role="status" className="text-sm text-muted-foreground">Loading your entry. You can keep typing.</p>
      )}
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
