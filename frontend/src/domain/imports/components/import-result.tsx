import { useEffect, useRef } from 'react'

import type { ImportBatchDto } from '@/api/generated/models/types/import-batch-dto'
import { PageHeading } from '@/components/page-heading'
import { Link } from '@/components/ui/link'
import { buttonVariants } from '@/components/ui/button-variants'
import { cn } from '@/lib/utils'

import { agree, daysWord, entriesWord, momentOf } from './import-words'

export function ImportResult({ batch }: { batch: ImportBatchDto }) {
  const headingRef = useRef<HTMLHeadingElement>(null)
  useEffect(() => headingRef.current?.focus(), [])
  const result = batch.result
  const acceptedAt = batch.acceptedAt ? momentOf(batch.acceptedAt) : null
  const lines = result
    ? [
        { count: result.sessionsMade, text: `${entriesWord(result.sessionsMade)} made in your journal` },
        {
          count: result.entriesRejected,
          text: `${entriesWord(result.entriesRejected)} left out because you rejected ${agree(result.entriesRejected, 'it or its day', 'them or their day')}`,
        },
        { count: result.daysRejected, text: `${daysWord(result.daysRejected)} that you rejected` },
        { count: result.skippedInJournal, text: `${entriesWord(result.skippedInJournal)} skipped because your journal has ${agree(result.skippedInJournal, 'it', 'them')}` },
        {
          count: result.skippedDeleted,
          text: `${entriesWord(result.skippedDeleted)} skipped because you deleted ${agree(result.skippedDeleted, 'it', 'them')} from your journal`,
        },
      ]
    : []

  return (
    <>
      <div className="flex flex-col gap-2">
        <PageHeading ref={headingRef}>{batch.name}</PageHeading>
        <p className="text-sm text-muted-foreground">
          This batch is in your journal.{acceptedAt && ` Accepted on ${acceptedAt}.`}
        </p>
      </div>
      <ul aria-label="Result of the accept" className="flex flex-col divide-y rounded-lg border">
        {lines.map(({ count, text }) => (
          <li key={text} className={cn('p-3 text-sm', count === 0 && 'text-muted-foreground')}>{text}</li>
        ))}
      </ul>
      <Link to="/" className={buttonVariants({ className: 'self-start max-sm:h-11' })}>
        Go to your journal
      </Link>
    </>
  )
}
