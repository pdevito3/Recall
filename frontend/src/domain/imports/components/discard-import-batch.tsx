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
  onDiscarded?: () => void
  onSettled?: () => void
}

export function DiscardImportBatch({ batch, onDiscarded, onSettled }: DiscardImportBatchProps) {
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
          <ResponsiveDialogTitle className="wrap-anywhere">Discard {batch.name}?</ResponsiveDialogTitle>
          <ResponsiveDialogDescription>
            This batch and its {entriesWord(batch.entryCount)} leave Recall, and nothing of it reaches your journal. You cannot undo a discard.
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
