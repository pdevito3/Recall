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
            {entryCount === 0 ? 'This batch is deleted.' : `This batch and its ${entriesWord(entryCount)} are deleted.`} Nothing of it goes into your journal. You
            cannot undo a discard.
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
