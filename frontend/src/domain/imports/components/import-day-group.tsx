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
  onEntryGone: () => void
}

export function ImportDayGroup({ batchId, batchZone, day, entries, onEntryGone }: ImportDayGroupProps) {
  const review = useReviewImportDay(batchId)
  const heading = dayOf(day.date)
  const headingId = `import-day-${day.date}`

  return (
    <section aria-labelledby={headingId} className="flex flex-col gap-2">
      <div className="sticky top-0 z-10 flex items-center justify-between gap-3 bg-background py-1">
        <div className="flex min-w-0 flex-wrap items-baseline gap-x-2">
          <h2 id={headingId} className="text-sm font-semibold text-muted-foreground">{heading}</h2>
          <span className="text-xs text-muted-foreground">
            {entriesWord(day.entryCount)}
            {day.rejected ? ', rejected with the day' : day.rejectedCount > 0 && `, ${countOf(day.rejectedCount)} rejected`}
          </span>
        </div>
        <Button
          variant="ghost"
          size="sm"
          className="-me-2.5 max-sm:h-11"
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
            <ImportEntryCard key={entry.clientSessionId} batchId={batchId} batchZone={batchZone} entry={entry} dayIsRejected={day.rejected} onGone={onEntryGone} />
          ))}
        </ul>
      )}
    </section>
  )
}
