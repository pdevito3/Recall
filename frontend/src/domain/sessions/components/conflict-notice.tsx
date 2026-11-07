import { Button } from '@/components/ui/button'

import type { DraftConflict } from '../autosave/draft-saver'

const messages: Record<NonNullable<DraftConflict>, string> = {
  wordsMovedToHistory: 'A newer save, or a recording, changed this entry first. The words on this page are safe in History.',
  discardedInAnotherTab: 'Another tab took these words, and you may have discarded them there. They may now be only on this page.',
}

const wrapping = 'h-auto min-h-8 max-w-full py-1.5 whitespace-normal'

interface ConflictNoticeProps {
  conflict: NonNullable<DraftConflict>
  onShowNewest: () => void
  onKeep: () => void
}

export function ConflictNotice({ conflict, onShowNewest, onKeep }: ConflictNoticeProps) {
  return (
    <div role="alert" className="flex flex-col gap-3 rounded-lg border bg-muted p-3 text-sm dark:bg-card wrap-anywhere">
      <p>{messages[conflict]}</p>
      <div className="flex flex-wrap gap-2">
        <Button variant="outline" className={wrapping} onPress={onShowNewest}>Show the newest words</Button>
        <Button variant="ghost" className={wrapping} onPress={onKeep}>Add my words to the newest words</Button>
      </div>
    </div>
  )
}
