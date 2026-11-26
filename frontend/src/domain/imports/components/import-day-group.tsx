import type { ImportDayDto } from '@/api/generated/models/types/import-day-dto'
import type { ImportEntryDto } from '@/api/generated/models/types/import-entry-dto'
import { Button } from '@/components/ui/button'

import { useReviewImportDay } from '../apis/imports'
import { ImportEntryCard } from './import-entry-card'
import { countOf, dayOf, entriesWord } from './import-words'

interface ImportDayGroupProps {
  batchId: string
  batchZone: string
  day: ImportDayDto
  entries: ImportEntryDto[]
  /** Runs when an entry or this day leaves the page, so the focus can go to the heading of the batch. */
  onGone: () => void
}

export function ImportDayGroup({ batchId, batchZone, day, entries, onGone }: ImportDayGroupProps) {
  // A kept day with no entry is not a day of the batch (IMP-31), so the read after the review takes it off the page.
  const leavesPage = day.rejected && day.entryCount === 0
  const review = useReviewImportDay(batchId, { onReviewed: leavesPage ? onGone : undefined })
  const heading = dayOf(day.date)
  const headingId = `import-day-${day.date}`

  return (
    <section aria-labelledby={headingId} className="flex flex-col gap-2">
      <div className="sticky top-0 z-10 flex items-center justify-between gap-3 bg-background py-1">
        <div className="flex min-w-0 flex-wrap items-baseline gap-x-2">
          <h2 id={headingId} className="text-sm font-semibold text-muted-foreground">{heading}</h2>
          <span className="text-xs text-muted-foreground">
            {entriesWord(day.entryCount)}
            {day.rejected ? ', day rejected' : day.rejectedCount > 0 && `, ${countOf(day.rejectedCount)} rejected`}
          </span>
        </div>
        <Button
          variant="ghost"
          size="sm"
          className="max-sm:h-11"
          aria-label={`${day.rejected ? 'Keep' : 'Reject'} ${heading}`}
          isPending={review.isPending}
          onPress={() => review.mutate({ date: day.date, rejected: !day.rejected })}
        >
          {day.rejected ? 'Keep day' : 'Reject day'}
        </Button>
      </div>
      {entries.length === 0 ? (
        <p className="text-sm text-muted-foreground">This day has no entries. While it is rejected, each entry that the import adds to it stays out.</p>
      ) : (
        <ul aria-label={`Entries of ${heading}`} className="flex flex-col gap-2">
          {entries.map((entry) => (
            <ImportEntryCard key={entry.clientSessionId} batchId={batchId} batchZone={batchZone} entry={entry} dayIsRejected={day.rejected} onGone={onGone} />
          ))}
        </ul>
      )}
    </section>
  )
}
