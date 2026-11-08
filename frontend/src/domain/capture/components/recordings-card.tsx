import type { RecordingDto } from '@/api/generated/models/types/recording-dto'
import { QueryError } from '@/components/query-error'
import { cn } from '@/lib/utils'

import { useSessionRecordings } from '../apis/get-session-recordings'

const STATUS_TEXT: Record<RecordingDto['status'], string> = {
  Receiving: 'The audio is on its way to the server.',
  Received: 'The audio is stored. The text comes next.',
  Transcribing: 'The audio turns into text now.',
  Transcribed: 'The text of this audio is done.',
  Failed: 'No text came from this audio.',
}

/** The Recordings of one entry, with what went wrong for each one that has a problem. An entry with no Recording shows nothing. */
export function RecordingsCard({ sessionId }: { sessionId: string }) {
  const recordings = useSessionRecordings(sessionId)

  // Most entries are typed, so a card that says it loads would show on each of them and then go away.
  if (recordings.isPending) return null
  if (recordings.isError) {
    return <QueryError title="The recordings of this entry did not load." error={recordings.error} onRetry={() => void recordings.refetch()} />
  }
  if (recordings.data.length === 0) return null

  return (
    <section aria-labelledby="entry-recordings" className="flex flex-col gap-3">
      <h2 id="entry-recordings" className="text-lg font-semibold">Recordings</h2>
      <ul className="flex flex-col gap-2">
        {recordings.data.map((recording, index) => (
          <li key={recording.id} className="flex flex-col gap-1 rounded-lg border p-3 text-sm">
            <span className="font-medium">Recording {index + 1}</span>
            <span>{STATUS_TEXT[recording.status]}</span>
            {recording.problemText && (
              <span className={cn('wrap-anywhere', recording.status === 'Failed' ? 'text-destructive' : 'text-muted-foreground')}>
                {recording.problemText}
              </span>
            )}
          </li>
        ))}
      </ul>
    </section>
  )
}
