import { Button } from '@/components/ui/button'

import type { DraftConflict } from '../autosave/draft-saver'

const messages: Record<NonNullable<DraftConflict>, string> = {
  wordsMovedToHistory: 'A newer save from another tab or device holds this entry now. The words on this page are safe in History.',
  replacedUnseenSave: 'These words replaced a save that this page had not shown. The other save is safe in History.',
}

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
        {conflict === 'wordsMovedToHistory' && <Button variant="outline" onPress={onShowNewest}>Show the newest words</Button>}
        <Button variant={conflict === 'wordsMovedToHistory' ? 'ghost' : 'outline'} onPress={onKeep}>Keep these words</Button>
      </div>
    </div>
  )
}
