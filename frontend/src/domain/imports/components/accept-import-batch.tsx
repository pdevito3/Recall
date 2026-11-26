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

import { useAcceptImportBatch } from '../apis/imports'
import type { ImportCounts } from '../pages-of'
import { entriesWord } from './import-words'

interface AcceptImportBatchProps {
  batchId: string
  counts: ImportCounts
  version: number
}

export function AcceptImportBatch({ batchId, counts, version }: AcceptImportBatchProps) {
  const [isOpen, setIsOpen] = useState(false)
  const accept = useAcceptImportBatch(batchId)
  const problem = accept.error && messagesOf(accept.error)

  const open = (next: boolean) => {
    if (next) accept.reset()
    setIsOpen(next)
  }

  return (
    <>
      <Button className="shrink-0" onPress={() => open(true)}>Accept</Button>
      <ResponsiveDialog role="alertdialog" isOpen={isOpen} onOpenChange={open} isDismissable={!accept.isPending}>
        <ResponsiveDialogHeader>
          <ResponsiveDialogTitle>Accept this batch?</ResponsiveDialogTitle>
          <ResponsiveDialogDescription>
            {entriesWord(counts.kept)} go into your journal, and {entriesWord(counts.rejected)} that you rejected stay out. After the accept, the batch cannot change.
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
          <Button isPending={accept.isPending} onPress={() => accept.mutate(version)}>
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
