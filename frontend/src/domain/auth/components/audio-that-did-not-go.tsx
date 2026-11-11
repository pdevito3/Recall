import type { StoredCapture } from '@/domain/capture/audio-store'
import { clock } from '@/domain/capture/audio-time'
import { waitsOnlyForItsEnd } from '@/domain/capture/capture-view'
import { unsentMsOf } from '@/domain/capture/upload-plan'
import { cn } from '@/lib/utils'

export function AudioThatDidNotGo({ audio, className }: { audio: StoredCapture[]; className?: string }) {
  return (
    <ul aria-label="Audio that did not reach the server" className={cn('space-y-2 rounded-lg border p-3 text-sm', className)}>
      {audio.map((capture) => (
        <li key={capture.clientRecordingId}>
          A recording of {clock(capture.durationMs)}.{' '}
          {waitsOnlyForItsEnd(capture) ? 'The server has all of it, and has not started on the words yet.' : `The server lacks ${clock(unsentMsOf(capture))} of it.`}
          {capture.problem?.kind === 'refused' && <p className="text-destructive">The server refused this audio.</p>}
        </li>
      ))}
    </ul>
  )
}
