import type { ImportBatchDto } from '@/api/generated/models/types/import-batch-dto'
import { Notification } from '@/components/notification'
import { Button } from '@/components/ui/button'
import {
  ResponsiveDialog,
  ResponsiveDialogClose,
  ResponsiveDialogDescription,
  ResponsiveDialogFooter,
  ResponsiveDialogHeader,
  ResponsiveDialogTitle,
  ResponsiveDialogTrigger,
} from '@/components/ui/responsive-dialog'

import { useDiscardImportBatch } from '../apis/imports'
import { entriesWord } from './import-words'

interface DiscardImportBatchProps {
  batch: ImportBatchDto
  /** The entries that the discard deletes. The detail page gives the count of the days that it read, which is newer than the list. */
  entryCount?: number
  onDiscarded?: () => void
  onSettled?: () => void
}

const discardTextOf = (entryCount: number) => {
  if (entryCount === 0) return 'Discard deletes this batch, which has no entries.'
  if (entryCount === 1) return 'Discard deletes this batch and its 1 entry. The entry does not go into your journal.'
  return `Discard deletes this batch and its ${entriesWord(entryCount)}. None of them go into your journal.`
}

export function DiscardImportBatch({ batch, entryCount = batch.entryCount, onDiscarded, onSettled }: DiscardImportBatchProps) {
  const discard = useDiscardImportBatch()

  const handleDiscard = () =>
    discard.mutate(batch.id, {
      onSuccess: () => {
        Notification.success('Batch discarded')
        onDiscarded?.()
      },
      onSettled,
    })

  return (
    <ResponsiveDialogTrigger>
      <Button variant="outline" className="shrink-0" aria-label={`Discard ${batch.name}`} isPending={discard.isPending}>
        Discard
      </Button>
      <ResponsiveDialog role="alertdialog">
        <ResponsiveDialogHeader>
          <ResponsiveDialogTitle className="wrap-anywhere">Discard “{batch.name}”?</ResponsiveDialogTitle>
          <ResponsiveDialogDescription>
            {discardTextOf(entryCount)} You cannot undo a discard.
          </ResponsiveDialogDescription>
        </ResponsiveDialogHeader>
        <ResponsiveDialogFooter>
          <ResponsiveDialogClose>Keep batch</ResponsiveDialogClose>
          <ResponsiveDialogClose variant="destructive" onPress={handleDiscard}>
            Discard batch
          </ResponsiveDialogClose>
        </ResponsiveDialogFooter>
      </ResponsiveDialog>
    </ResponsiveDialogTrigger>
  )
}
