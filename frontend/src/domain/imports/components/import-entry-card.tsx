import { parseTime, type Time } from '@internationalized/date'
import { useEffect, useId, useRef, useState } from 'react'

import type { ImportEntryDto } from '@/api/generated/models/types/import-entry-dto'
import type { ImportEntryReviewDto } from '@/api/generated/models/types/import-entry-review-dto'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { JollyTimeField } from '@/components/ui/datefield'
import { JournalEditor } from '@/domain/sessions/editor/journal-editor'
import { JournalStaticText } from '@/domain/sessions/editor/journal-static-text'
import { refusalOf } from '@/hooks/use-server-errors'
import { cn } from '@/lib/utils'

import { useReviewImportEntry } from '../apis/imports'
import { entryTimeOf } from '../entry-time'

interface ImportEntryCardProps {
  batchId: string
  batchZone: string
  entry: ImportEntryDto
  dayIsRejected: boolean
  onGone: () => void
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
  const review = useReviewImportEntry(batchId, { onOutdated: onGone })
  const { time, zoneLabel, editZone } = entryTimeOf(entry, batchZone)
  const isEdited = entry.review.words != null || entry.review.time != null
  const label = `Entry at ${time}`

  if (isEditing) {
    return (
      <li aria-label={label} className="rounded-lg border bg-card p-3">
        <ImportEntryEdit batchId={batchId} entry={entry} zone={editZone} onDone={() => setIsEditing(false)} onGone={onGone} />
      </li>
    )
  }

  return (
    <li aria-label={label} className="flex flex-col gap-2 rounded-lg border bg-card p-3">
      <div className="flex flex-wrap items-baseline gap-x-2 text-xs text-muted-foreground">
        <span>{time}</span>
        {zoneLabel && <span className="wrap-anywhere">{zoneLabel}</span>}
        {entry.review.time == null && entry.time == null && <span>No time given. Set from its place in the day.</span>}
        {isEdited && <span>Edited</span>}
        {entry.isRejected && (
          <span className="font-medium text-destructive">{entry.review.rejected ? 'Rejected' : 'Rejected with the day'}</span>
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
      {entry.wordsChangedAfterEdit && (
        <ChangeNote>The import changed the words after your edit. The accept keeps your edit. To use the new words, choose Edit, then Use the import words.</ChangeNote>
      )}
      {entry.timeChangedAfterEdit && (
        <ChangeNote>The import changed the time after your edit. The accept keeps your time. To use the new time, choose Edit, then Use the import time.</ChangeNote>
      )}
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
            onPress={() => review.mutate(reviewOf(entry, { rejected: !entry.review.rejected }))}
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
  /** The zone that a typed time is in. */
  zone: string
  onDone: () => void
  onGone: () => void
}

function ImportEntryEdit({ batchId, entry, zone, onDone, onGone }: ImportEntryEditProps) {
  const review = useReviewImportEntry(batchId, { inForm: true, onOutdated: onGone })
  const shownTime = entry.review.time ?? entry.time ?? null
  const [words, setWords] = useState<string | null>(null)
  const [time, setTime] = useState<Time | null>(shownTime ? parseTime(shownTime) : null)
  const timeChanged = (time ? hhmm(time) : null) !== shownTime
  const hasChanges = words !== null || timeChanged
  const refusal = review.error ? refusalOf(review.error, ['words']) : null
  const wordsError = refusal?.byField.words
  const problem = refusal?.problem
  const wordsId = useId()
  const wordsErrorId = `${wordsId}-error`
  const importValuesOffId = `${wordsId}-import-values-off`
  const hasImportValues = entry.review.words != null || entry.review.time != null
  useEffect(() => {
    if (wordsError) document.getElementById(wordsId)?.focus()
  }, [wordsError, wordsId])

  const send = (patch: Partial<ImportEntryReviewDto>) => review.mutate(reviewOf(entry, patch), { onSuccess: onDone })
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
        <span aria-hidden="true" className={cn('text-sm leading-none font-medium select-none', wordsError && 'text-destructive')}>Words</span>
        <JournalEditor
          initialText={entry.document}
          revision={0}
          label="Words"
          id={wordsId}
          errorId={wordsError ? wordsErrorId : undefined}
          autoFocus
          onChange={(text) => setWords(text)}
        />
        {wordsError && (
          <p id={wordsErrorId} role="alert" className="text-sm text-destructive">
            {wordsError}
          </p>
        )}
      </div>
      <JollyTimeField
        label="Time"
        description={
          entry.time == null
            ? `In ${zone}. Leave it empty to keep the place of the entry in its day.`
            : `In ${zone}. Leave it empty to use the import time.`
        }
        value={time}
        onChange={setTime}
        className="[&>[role=group]]:max-w-60"
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
          <Button
            variant="outline"
            className="max-sm:h-11"
            isDisabled={review.isPending || hasChanges}
            aria-describedby={hasChanges ? importValuesOffId : undefined}
            onPress={() => send({ words: null })}
          >
            Use the import words
          </Button>
        )}
        {entry.review.time != null && (
          <Button
            variant="outline"
            className="max-sm:h-11"
            isDisabled={review.isPending || hasChanges}
            aria-describedby={hasChanges ? importValuesOffId : undefined}
            onPress={() => send({ time: null })}
          >
            Use the import time
          </Button>
        )}
      </div>
      {hasImportValues && hasChanges && (
        <p id={importValuesOffId} className="text-sm text-muted-foreground">
          Save or cancel your change first.
        </p>
      )}
    </form>
  )
}
