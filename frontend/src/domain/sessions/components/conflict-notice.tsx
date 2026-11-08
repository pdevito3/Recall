import { useId, type Ref } from 'react'

import { Button } from '@/components/ui/button'

import type { DraftConflict } from '../autosave/draft-saver'

const messages: Record<NonNullable<DraftConflict>, string> = {
  wordsMovedToHistory: 'A newer save, or a recording, changed this entry after your words saved. The words on this page are safe in History.',
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

interface EditedElsewhereNoticeProps {
  /** True when this tab edited the entry until another tab took it. */
  hasLost: boolean
  onEditHere: () => void
  editHereRef: Ref<HTMLButtonElement>
}

/** The button names the notice, so a screen reader reads why when the focus comes to it after another tab took the entry. */
export function EditedElsewhereNotice({ hasLost, onEditHere, editHereRef }: EditedElsewhereNoticeProps) {
  const why = useId()
  return (
    <div role="status" className="flex flex-col gap-2 rounded-lg border bg-muted p-3 text-sm dark:bg-card wrap-anywhere sm:flex-row sm:items-center sm:justify-between">
      <p id={why}>
        {hasLost ? 'You now edit this entry in another tab. This tab shows the words and saves nothing.' : 'This entry is open in another tab. This tab shows the words and saves nothing.'}
        {' '}Choose Edit here to write in this tab.
      </p>
      <Button ref={editHereRef} aria-describedby={why} variant="outline" className={`${wrapping} self-end sm:self-auto`} onPress={onEditHere}>Edit here</Button>
    </div>
  )
}

export const mergedNote = 'Another tab, device, or recording changed this entry while you wrote. Your words are now in the newest words, and the words before the merge are in History.'

/** A merge changes the words on the page, so the note stays until the person dismisses it. */
export function MergedNotice({ onDismiss }: { onDismiss: () => void }) {
  return (
    <div role="status" className="flex flex-col gap-2 rounded-lg border bg-muted p-3 text-sm dark:bg-card wrap-anywhere sm:flex-row sm:items-center sm:justify-between">
      <p>{mergedNote}</p>
      <Button variant="ghost" className={`${wrapping} self-end sm:self-auto`} onPress={onDismiss}>Dismiss</Button>
    </div>
  )
}
