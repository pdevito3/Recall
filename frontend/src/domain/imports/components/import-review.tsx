import { useNavigate } from '@tanstack/react-router'
import { useRef } from 'react'

import type { ImportBatchDto } from '@/api/generated/models/types/import-batch-dto'
import type { ImportEntryDto } from '@/api/generated/models/types/import-entry-dto'
import { PageHeading } from '@/components/page-heading'
import { QueryError } from '@/components/query-error'
import { PagerButtons } from '@/components/ui/pager'
import { isProblemDetailsError } from '@/lib/problem-details'

import { useImportDays, useImportEntries } from '../apis/imports'
import { countsOf, pagesOf, type ImportPage } from '../pages-of'
import { AcceptImportBatch } from './accept-import-batch'
import { DiscardImportBatch } from './discard-import-batch'
import { ImportDayGroup } from './import-day-group'
import { ImportBatchMissing } from './import-batch-missing'
import { countOf, dateSpanOf, entriesWord, momentOf, zoneLabelOf } from './import-words'

interface ImportReviewProps {
  batch: ImportBatchDto
  pageNumber: number
}

export function ImportReview({ batch, pageNumber }: ImportReviewProps) {
  const days = useImportDays(batch.id)
  const navigate = useNavigate()
  const headingRef = useRef<HTMLHeadingElement>(null)

  if (days.isError && !days.data) {
    return isProblemDetailsError(days.error) && days.error.status === 404 ? (
      <ImportBatchMissing />
    ) : (
      <QueryError title="This batch did not load." error={days.error} onRetry={() => void days.refetch()} />
    )
  }

  const pages = days.data ? pagesOf(days.data.days) : []
  const shown = Math.min(pageNumber, Math.max(pages.length, 1))
  const page = pages[shown - 1] as ImportPage | undefined
  const counts = days.data ? countsOf(days.data.days) : null
  const changePage = (next: number) => {
    void navigate({ to: '.', search: { page: next } })
    headingRef.current?.focus()
  }

  return (
    <>
      <div className="flex flex-col gap-2">
        <PageHeading ref={headingRef}>{batch.name}</PageHeading>
        <p className="text-sm text-muted-foreground wrap-anywhere">
          Opened on {momentOf(batch.openedAt)}. Times are in {zoneLabelOf(batch.timeZone)}, unless an entry names its own time zone.
        </p>
      </div>
      <div className="flex flex-wrap items-center justify-between gap-3 rounded-lg border bg-card p-3">
        {counts ? (
          <div className="flex flex-col gap-1">
            <p aria-label="Counts of this batch" className="text-sm">
              <span className="font-medium">{entriesWord(counts.entries)}</span>
              <span className="text-muted-foreground">: {countOf(counts.kept)} kept, {countOf(counts.rejected)} rejected</span>
            </p>
            {counts.kept === 0 && (
              <p id={nothingKeptReasonId} className="text-sm text-muted-foreground">
                {counts.entries === 0 ? 'Wait for the agent to add entries, or discard this batch.' : 'Keep an entry to accept this batch, or discard it.'}
              </p>
            )}
          </div>
        ) : (
          <p role="status" aria-label="Loading the counts" className="text-sm text-muted-foreground">Loading the counts</p>
        )}
        <div className="ml-auto flex flex-wrap justify-end gap-2">
          <DiscardImportBatch batch={batch} entryCount={counts?.entries} onDiscarded={() => void navigate({ to: '/imports' })} />
          {counts && days.data && (
            <AcceptImportBatch batchId={batch.id} batchName={batch.name} counts={counts} version={days.data.version} nothingKeptReasonId={nothingKeptReasonId} />
          )}
        </div>
      </div>
      {days.isPending && <p role="status" aria-label="Loading the days" className="text-muted-foreground">Loading the days</p>}
      {days.data && pages.length === 0 && (
        <p className="text-muted-foreground">This batch has no entries yet. The agent adds them, then you review them here.</p>
      )}
      {page && (
        <>
          {pages.length > 1 && <ImportPager label="Pages of this batch" page={page} shown={shown} total={pages.length} onPageChange={changePage} />}
          <ImportPageEntries batch={batch} page={page} onGone={() => headingRef.current?.focus()} />
          {pages.length > 1 && <ImportPager label="Pages of this batch, at the end" page={page} shown={shown} total={pages.length} onPageChange={changePage} />}
        </>
      )}
    </>
  )
}

const nothingKeptReasonId = 'import-nothing-kept'

interface ImportPagerProps {
  label: string
  page: ImportPage
  shown: number
  total: number
  onPageChange: (pageNumber: number) => void
}

/** The pager of a batch. A page holds whole days (IMP-35), so the pager names the dates of the page. */
function ImportPager({ label, page, shown, total, onPageChange }: ImportPagerProps) {
  return (
    <nav aria-label={label} className="flex items-center justify-end gap-2">
      <span className="mr-auto text-sm text-muted-foreground">
        Page {shown} of {total}: {dateSpanOf(page.from, page.to)}
      </span>
      <PagerButtons hasPrevious={shown > 1} hasNext={shown < total} onPrevious={() => onPageChange(shown - 1)} onNext={() => onPageChange(shown + 1)} />
    </nav>
  )
}

interface ImportPageEntriesProps {
  batch: ImportBatchDto
  page: ImportPage
  onGone: () => void
}

function ImportPageEntries({ batch, page, onGone }: ImportPageEntriesProps) {
  const entries = useImportEntries(batch.id, page.from, page.to)

  if (entries.isPending) {
    return <p role="status" aria-label="Loading the entries" className="text-muted-foreground">Loading the entries</p>
  }
  if (entries.isError && !entries.data) {
    return <QueryError title="The entries did not load." error={entries.error} onRetry={() => void entries.refetch()} />
  }

  const byDate = new Map<string, ImportEntryDto[]>()
  for (const entry of entries.data.entries) byDate.set(entry.date, [...(byDate.get(entry.date) ?? []), entry])
  return (
    <div className="flex flex-col gap-6">
      {page.days.map((day) => (
        <ImportDayGroup key={day.date} batchId={batch.id} batchZone={batch.timeZone} day={day} entries={byDate.get(day.date) ?? noEntries} onGone={onGone} />
      ))}
    </div>
  )
}

const noEntries: ImportEntryDto[] = []
