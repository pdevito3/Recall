import type { UseQueryResult } from '@tanstack/react-query'
import type { RefObject } from 'react'

import type { ImportBatchDto } from '@/api/generated/models/types/import-batch-dto'
import { QueryError } from '@/components/query-error'
import { Badge } from '@/components/ui/badge'
import { Link } from '@/components/ui/link'

import { DiscardImportBatch } from './discard-import-batch'
import { entriesWord, momentOf, zoneLabelOf } from './import-words'

interface ImportBatchListProps {
  batches: UseQueryResult<ImportBatchDto[]>
  headingRef: RefObject<HTMLHeadingElement | null>
}

export function ImportBatchList({ batches, headingRef }: ImportBatchListProps) {
  if (batches.isPending) {
    return <p role="status" aria-label="Loading your import batches" className="text-muted-foreground">Loading the batches</p>
  }
  if (batches.isError) {
    return <QueryError title="The import batches did not load." error={batches.error} onRetry={() => void batches.refetch()} />
  }
  if (batches.data.length === 0) {
    return (
      <p className="max-w-prose text-muted-foreground">
        You have no import batches. When an agent opens a batch, you review it here.
      </p>
    )
  }

  return (
    <ul aria-label="Your import batches" className="flex flex-col divide-y rounded-lg border">
      {batches.data.map((batch) => (
        <li key={batch.id} aria-label={batch.name} className="flex flex-wrap items-start justify-between gap-x-3 gap-y-2 p-3">
          <div className="flex min-w-0 flex-1 basis-60 flex-col gap-1 text-sm">
            <div className="flex flex-wrap items-baseline gap-x-2">
              <Link
                to="/imports/$id"
                params={{ id: batch.id }}
                dir="auto"
                className="min-w-0 text-base font-medium text-primary wrap-anywhere underline underline-offset-4 hover:decoration-2 max-md:inline-flex max-md:min-h-11 max-md:items-center"
              >
                {batch.name}
              </Link>
              <Badge variant="secondary">{batch.state === 'Open' ? 'Open' : 'In your journal'}</Badge>
            </div>
            <span>
              {batch.result
                ? `${entriesWord(batch.result.sessionsMade)} made in your journal. Times in ${zoneLabelOf(batch.timeZone)}.`
                : `${entriesWord(batch.entryCount)}. Times in ${zoneLabelOf(batch.timeZone)}.`}
            </span>
            <span className="text-muted-foreground">
              Opened on {momentOf(batch.openedAt)}.{batch.acceptedAt && ` Accepted on ${momentOf(batch.acceptedAt)}.`}
            </span>
          </div>
          {batch.state === 'Open' && (
            <DiscardImportBatch batch={batch} onSettled={() => headingRef.current?.focus()} />
          )}
        </li>
      ))}
    </ul>
  )
}
