import { useEffect, useRef } from 'react'

import type { ImportBatchDto } from '@/api/generated/models/types/import-batch-dto'
import { PageHeading } from '@/components/page-heading'
import { Link } from '@/components/ui/link'
import { buttonVariants } from '@/components/ui/button-variants'
import { browserTimeZone, formatInstant } from '@/lib/zoned-time'

import { daysWord, entriesWord } from './import-words'

export function ImportResult({ batch }: { batch: ImportBatchDto }) {
  const headingRef = useRef<HTMLHeadingElement>(null)
  useEffect(() => headingRef.current?.focus(), [])
  const result = batch.result
  const acceptedAt = batch.acceptedAt
    ? formatInstant(new Date(batch.acceptedAt), browserTimeZone(), { year: 'numeric', month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' })
    : null
  const lines = result
    ? [
        `${entriesWord(result.sessionsMade)} made in your journal`,
        `${entriesWord(result.entriesRejected)} rejected`,
        `${daysWord(result.daysRejected)} rejected`,
        `${entriesWord(result.skippedInJournal)} skipped, because your journal has them`,
        `${entriesWord(result.skippedDeleted)} skipped, because you deleted them`,
      ]
    : []

  return (
    <>
      <PageHeading ref={headingRef}>{batch.name}</PageHeading>
      <p className="text-muted-foreground">
        This batch is in your journal.{acceptedAt && ` Accepted on ${acceptedAt}.`}
      </p>
      <ul aria-label="Result of the accept" className="flex flex-col divide-y rounded-lg border">
        {lines.map((line) => (
          <li key={line} className="p-3 text-sm">{line}</li>
        ))}
      </ul>
      <Link to="/" className={buttonVariants({ variant: 'outline', className: 'self-start' })}>
        Go to your journal
      </Link>
    </>
  )
}
