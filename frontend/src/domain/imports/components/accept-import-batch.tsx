import { useState } from 'react'

import { Button } from '@/components/ui/button'
import {
  ResponsiveDialog,
  ResponsiveDialogClose,
  ResponsiveDialogDescription,
  ResponsiveDialogFooter,
  ResponsiveDialogHeader,
  ResponsiveDialogTitle,
} from '@/components/ui/responsive-dialog'
import { refusalOf } from '@/hooks/use-server-errors'
import { isStaleVersionError } from '@/lib/problem-details'

import { useAcceptImportBatch, useIsReviewing } from '../apis/imports'
import type { ImportCounts } from '../pages-of'
import { agree, entriesWord } from './import-words'

interface AcceptImportBatchProps {
  batchId: string
  batchName: string
  counts: ImportCounts
  version: number
  /** The ID of the text that says why Accept is off when no entry is kept. */
  nothingKeptReasonId: string
}

export function AcceptImportBatch({ batchId, batchName, counts, version, nothingKeptReasonId }: AcceptImportBatchProps) {
  const [isOpen, setIsOpen] = useState(false)
  const accept = useAcceptImportBatch(batchId)
  const isReviewing = useIsReviewing(batchId)
  const isStale = isStaleVersionError(accept.error)
  const problem = accept.error && !isStale && messagesOf(accept.error)
  const hasNothingKept = counts.kept === 0

  const open = (next: boolean) => {
    if (next) accept.reset()
    setIsOpen(next)
  }

  return (
    <>
      <Button className="shrink-0" isDisabled={isReviewing || hasNothingKept} aria-describedby={hasNothingKept ? nothingKeptReasonId : undefined} onPress={() => open(true)}>
        Accept
      </Button>
      <ResponsiveDialog role="alertdialog" isOpen={isOpen} onOpenChange={open} isDismissable={!accept.isPending}>
        <ResponsiveDialogHeader>
          <ResponsiveDialogTitle className="wrap-anywhere">Accept “{batchName}”?</ResponsiveDialogTitle>
          <p role="status" className="text-sm empty:hidden">
            {isStale && 'The batch changed. Recall read it again. Check the new counts, then accept the batch.'}
          </p>
          <ResponsiveDialogDescription>
            {entriesWord(counts.kept)} {agree(counts.kept, 'goes', 'go')} into your journal, and {entriesWord(counts.rejected)} that you rejected{' '}
            {agree(counts.rejected, 'stays', 'stay')} out. After the accept, the batch cannot change.
          </ResponsiveDialogDescription>
        </ResponsiveDialogHeader>
        {problem && (
          <div role="alert" className="mt-3 rounded-lg border border-destructive/20 bg-destructive/10 p-3 text-sm text-destructive wrap-anywhere">
            {problem.map((message, i) => (
              <p key={`${i}-${message}`}>{message}</p>
            ))}
          </div>
        )}
        <ResponsiveDialogFooter>
          <ResponsiveDialogClose isDisabled={accept.isPending}>Keep reviewing</ResponsiveDialogClose>
          <Button isPending={accept.isPending} isDisabled={isReviewing || hasNothingKept} onPress={() => accept.mutate(version)}>
            Accept batch
          </Button>
        </ResponsiveDialogFooter>
      </ResponsiveDialog>
    </>
  )
}

function messagesOf(error: Error): string[] {
  const problem = refusalOf(error, []).problem
  if (problem?.kind === 'refused') return problem.messages
  return [error.message]
}
