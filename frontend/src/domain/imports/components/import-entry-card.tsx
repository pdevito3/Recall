import { parseTime, type Time } from '@internationalized/date'
import { useEffect, useRef, useState } from 'react'

import type { ImportEntryDto } from '@/api/generated/models/types/import-entry-dto'
import type { ImportEntryReviewDto } from '@/api/generated/models/types/import-entry-review-dto'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { JollyTimeField } from '@/components/ui/datefield'
import { JournalEditor } from '@/domain/sessions/editor/journal-editor'
import { JournalStaticText } from '@/domain/sessions/editor/journal-static-text'
import { refusalOf } from '@/hooks/use-server-errors'
import { formatInstant, parseTimeZone, timeZoneLabel, type TimeZone } from '@/lib/zoned-time'
import { cn } from '@/lib/utils'

import { isOutdated, useReviewImportEntry } from '../apis/imports'

interface ImportEntryCardProps {
  batchId: string
  batchZone: string
  entry: ImportEntryDto
  dayIsRejected: boolean
  onGone: () => void
}

const zoneOf = (zone: string): TimeZone => {
  try {
    return parseTimeZone(zone)
  } catch {
    return 'UTC' as TimeZone
  }
}

const reviewOf = (entry: ImportEntryDto, patch: Partial<ImportEntryReviewDto> = {}): ImportEntryReviewDto => ({
  clientSessionId: entry.clientSessionId,
  rejected: entry.review.rejected,
  words: entry.review.words ?? null,
  time: entry.review.time ?? null,
  ...patch,
})

export function ImportEntryCard({ batchId, batchZone, entry, dayIsRejected, onGone }: ImportEntryCardProps) {
  const [isEditing, setIsEditing] = useState(false)
  const editButton = useRef<HTMLButtonElement>(null)
  const wasEditing = useRef(false)
  useEffect(() => {
    if (wasEditing.current && !isEditing) editButton.current?.focus()
    wasEditing.current = isEditing
  }, [isEditing])
  const review = useReviewImportEntry(batchId)
  const zone = zoneOf(entry.timeZone ?? batchZone)
  const time = formatInstant(new Date(entry.startsAt), zone, { hour: 'numeric', minute: '2-digit' })
  const isEdited = entry.review.words != null || entry.review.time != null
  const label = `Entry at ${time}`

  if (isEditing) {
    return (
      <li aria-label={label} className="rounded-lg border bg-card p-3">
        <ImportEntryEdit batchId={batchId} entry={entry} zone={zone} onDone={() => setIsEditing(false)} onGone={onGone} />
      </li>
    )
  }

  return (
    <li aria-label={label} className="flex flex-col gap-2 rounded-lg border bg-card p-3">
      <div className="flex flex-wrap items-baseline gap-x-2 text-xs text-muted-foreground">
        <span className="font-medium text-foreground">{time}</span>
        {entry.timeZone && entry.timeZone !== batchZone && <span className="wrap-anywhere">{timeZoneLabel(zone)}</span>}
        {entry.review.time == null && entry.time == null && <span>Time from its place in the day</span>}
        {isEdited && <span>Edited</span>}
        {entry.isRejected && (
          <span className="font-medium text-destructive">{entry.review.rejected ? 'Rejected' : 'Rejected with its day'}</span>
        )}
      </div>
      <div className={cn('min-w-0', entry.isRejected && 'text-muted-foreground')}>
        <JournalStaticText text={entry.document} />
      </div>
      {(entry.moods.length > 0 || entry.topics.length > 0) && (
        <ul aria-label="Moods and topics" className="flex flex-wrap gap-1">
          {entry.moods.map((mood) => (
            <li key={`mood-${mood}`}>
              <Badge variant="outline" className="h-auto max-w-full whitespace-normal wrap-anywhere">{mood}</Badge>
            </li>
          ))}
          {entry.topics.map((topic) => (
            <li key={`topic-${topic}`}>
              <Badge variant="secondary" className="h-auto max-w-full whitespace-normal wrap-anywhere">{topic}</Badge>
            </li>
          ))}
        </ul>
      )}
      {entry.wordsChangedAfterEdit && <ChangeNote>The import changed the words after your edit. The accept keeps your edit.</ChangeNote>}
      {entry.timeChangedAfterEdit && <ChangeNote>The import changed the time after your edit. The accept keeps your time.</ChangeNote>}
      <div className="flex flex-wrap gap-2">
        <Button ref={editButton} variant="outline" size="sm" className="max-sm:h-11" aria-label={`Edit the entry at ${time}`} onPress={() => setIsEditing(true)}>
          Edit
        </Button>
        {!dayIsRejected && (
          <Button
            variant="outline"
            size="sm"
            className="max-sm:h-11"
            aria-label={`${entry.review.rejected ? 'Keep' : 'Reject'} the entry at ${time}`}
            isPending={review.isPending}
            onPress={() => review.mutate(reviewOf(entry, { rejected: !entry.review.rejected }), { onError: (error) => isOutdated(error) && onGone() })}
          >
            {entry.review.rejected ? 'Keep' : 'Reject'}
          </Button>
        )}
      </div>
    </li>
  )
}

function ChangeNote({ children }: { children: string }) {
  return <p className="rounded-md border bg-muted px-2 py-1 text-sm">{children}</p>
}

const hhmm = (time: Time) => `${String(time.hour).padStart(2, '0')}:${String(time.minute).padStart(2, '0')}`

interface ImportEntryEditProps {
  batchId: string
  entry: ImportEntryDto
  zone: TimeZone
  onDone: () => void
  onGone: () => void
}

function ImportEntryEdit({ batchId, entry, zone, onDone, onGone }: ImportEntryEditProps) {
  const review = useReviewImportEntry(batchId, { inForm: true })
  const shownTime = entry.review.time ?? entry.time ?? null
  const [words, setWords] = useState<string | null>(null)
  const [time, setTime] = useState<Time | null>(shownTime ? parseTime(shownTime) : null)
  const timeChanged = (time ? hhmm(time) : null) !== shownTime
  const problem = review.error ? refusalOf(review.error, []).problem : null

  const send = (patch: Partial<ImportEntryReviewDto>) => review.mutate(reviewOf(entry, patch), { onSuccess: onDone, onError: (error) => isOutdated(error) && onGone() })
  const save = () =>
    send({
      ...(words !== null && { words }),
      ...(timeChanged && { time: time ? hhmm(time) : null }),
    })

  return (
    <form
      aria-label="Edit the entry"
      className="flex flex-col gap-3"
      onSubmit={(event) => {
        event.preventDefault()
        save()
      }}
    >
      <div className="flex flex-col gap-2">
        <span aria-hidden="true" className="text-sm leading-none font-medium select-none">Words</span>
        <JournalEditor initialText={entry.document} revision={0} label="Words" autoFocus onChange={(text) => setWords(text)} />
      </div>
      <JollyTimeField
        label="Time"
        description={`In ${timeZoneLabel(zone)}. Leave it empty to keep the place of the entry in its day.`}
        value={time}
        onChange={setTime}
        className="max-w-60"
      />
      {problem?.kind === 'refused' && (
        <div role="alert" className="rounded-lg border border-destructive/20 bg-destructive/10 p-3 text-sm text-destructive wrap-anywhere">
          {problem.messages.map((message, i) => (
            <p key={`${i}-${message}`}>{message}</p>
          ))}
        </div>
      )}
      <div className="flex flex-wrap gap-2">
        <Button type="submit" isPending={review.isPending} className="max-sm:h-11">Save</Button>
        <Button variant="outline" className="max-sm:h-11" onPress={onDone}>Cancel</Button>
        {entry.review.words != null && (
          <Button variant="ghost" className="max-sm:h-11" isDisabled={review.isPending} onPress={() => send({ words: null })}>
            Use the import words
          </Button>
        )}
        {entry.review.time != null && (
          <Button variant="ghost" className="max-sm:h-11" isDisabled={review.isPending} onPress={() => send({ time: null })}>
            Use the import time
          </Button>
        )}
      </div>
    </form>
  )
}
