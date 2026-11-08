import { memo, useEffect, useMemo, useRef } from 'react'
import { parseDate } from '@internationalized/date'

import type { SessionSummaryDto } from '@/api/generated/models/types/session-summary-dto'
import { QueryError } from '@/components/query-error'
import { Badge } from '@/components/ui/badge'
import { buttonVariants } from '@/components/ui/button-variants'
import { Link } from '@/components/ui/link'
import { usePeopleDirectory } from '@/domain/people/apis/people'
import { formatInstant, todayAt, type TimeZone } from '@/lib/zoned-time'

import { useSessionTimeline } from '../apis/get-session-timeline'
import { activityNone } from '../tags'
import { shownPreviewOf } from '../preview'
import { dayHeading, groupByDay, type DayGroup } from '../timeline-days'

const loadAheadPx = 1200

interface TimelineProps {
  zone: TimeZone
  hiddenSessionId: string | null
}

export function Timeline({ zone, hiddenSessionId }: TimelineProps) {
  const timeline = useSessionTimeline()
  const { hasNextPage, isFetchingNextPage, isFetchNextPageError, fetchNextPage } = timeline
  const directory = usePeopleDirectory().data
  const labels = useMemo(() => new Map(directory?.map((person) => [person.id, person.label])), [directory])
  const sentinel = useRef<HTMLDivElement>(null)
  const pageCount = timeline.data?.pages.length ?? 0

  // An observer tells only of a change. A new observer tells where the end of the list is after each page.
  useEffect(() => {
    if (!hasNextPage || isFetchNextPageError || !sentinel.current) return
    const observer = new IntersectionObserver(
      ([entry]) => {
        if (entry.isIntersecting && !isFetchingNextPage) void fetchNextPage()
      },
      { rootMargin: `${loadAheadPx}px` },
    )
    observer.observe(sentinel.current)
    return () => observer.disconnect()
  }, [hasNextPage, isFetchingNextPage, isFetchNextPageError, fetchNextPage, pageCount])

  const groups = useMemo(() => {
    const rows = timeline.data?.pages.flatMap((page) => page.items) ?? []
    return groupByDay(rows.filter((row) => row.id !== hiddenSessionId))
  }, [timeline.data, hiddenSessionId])

  if (timeline.isPending) {
    return <p role="status" aria-label="Loading your entries" className="text-muted-foreground">Loading your entries</p>
  }
  if (timeline.isLoadingError) {
    return <QueryError title="Your entries did not load." error={timeline.error} onRetry={() => timeline.refetch()} />
  }
  if (groups.length === 0) {
    return <p className="text-muted-foreground">Your entries show here. Write your first one above.</p>
  }

  const today = todayAt(zone, new Date())

  return (
    <div className="flex flex-col gap-6">
      {groups.map((group) => (
        <DaySection key={group.day} group={group} today={today.toString()} zone={zone} labels={labels} />
      ))}
      <div ref={sentinel}>
        {isFetchingNextPage && <p role="status" className="py-2 text-center text-sm text-muted-foreground">Loading more entries</p>}
        {timeline.isFetchNextPageError && (
          <QueryError title="More entries did not load." error={timeline.error} onRetry={() => fetchNextPage()} />
        )}
      </div>
    </div>
  )
}

interface DaySectionProps {
  group: DayGroup
  today: string
  zone: TimeZone
  labels: Map<string, string>
}

const DaySection = memo(function DaySection({ group, today, zone, labels }: DaySectionProps) {
  const headingId = `day-${group.day}`
  const heading = dayHeading(parseDate(group.day), parseDate(today))
  return (
    <section aria-labelledby={headingId}>
      <div className="sticky top-0 z-10 flex items-center justify-between gap-2 bg-background py-1">
        <h2 id={headingId} className="min-w-0 text-sm font-semibold text-muted-foreground">
          {heading}
        </h2>
        <Link
          to="/summaries"
          search={{ period: 'Day', date: group.day }}
          aria-label={`Summary of ${heading}`}
          className={buttonVariants({ variant: 'ghost', size: 'sm' })}
        >
          Summary
        </Link>
      </div>
      <ul className="flex flex-col gap-2">
        {group.rows.map((row) => (
          <TimelineRow key={row.id} row={row} zone={zone} labels={labels} />
        ))}
      </ul>
    </section>
  )
}, sameDaySection)

function sameDaySection(a: DaySectionProps, b: DaySectionProps) {
  return (
    a.today === b.today &&
    a.zone === b.zone &&
    a.labels === b.labels &&
    a.group.rows.length === b.group.rows.length &&
    a.group.rows.every((row, index) => row === b.group.rows[index])
  )
}

function TimelineRow({ row, zone, labels }: { row: SessionSummaryDto; zone: TimeZone; labels: Map<string, string> }) {
  const time = formatInstant(new Date(row.startedAt), zone, { hour: 'numeric', minute: '2-digit' })
  const people = row.personIds.flatMap((id) => labels.get(id) ?? [])
  const tags = new Set([
    ...(row.activity && row.activity !== activityNone ? [row.activity] : []),
    ...row.moods,
    ...row.topics.map((topic) => topic.name),
    ...people.map((label) => `@${label}`),
  ])
  return (
    <li className="[contain-intrinsic-size:auto_5.5rem] [content-visibility:auto]">
      <Link
        to="/sessions/$id"
        params={{ id: row.id }}
        className="flex min-h-11 flex-col gap-1 rounded-lg border bg-card p-3 outline-none hover:bg-accent focus-visible:ring-[3px] focus-visible:ring-ring/50"
      >
        <span className="text-xs text-muted-foreground">{time}</span>
        <span dir="auto" className="line-clamp-3 wrap-anywhere">
          {shownPreviewOf(row.preview) || <span className="text-muted-foreground">No words yet</span>}
        </span>
        {tags.size > 0 && (
          <span className="flex flex-wrap gap-1 pt-1">
            {[...tags].map((tag) => (
              <Badge key={tag} variant="secondary" className="max-w-full">
                <span className="truncate">{tag}</span>
              </Badge>
            ))}
          </span>
        )}
      </Link>
    </li>
  )
}
