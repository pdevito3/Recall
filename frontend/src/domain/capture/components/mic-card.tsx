import { HugeiconsIcon } from '@hugeicons/react'
import {
  AlertCircleIcon,
  CheckmarkCircle02Icon,
  CloudUploadIcon,
  HardDriveIcon,
  Loading03Icon,
  Mic01Icon,
  MicOff01Icon,
  SquareLock01Icon,
  StopIcon,
  WifiDisconnected01Icon,
} from '@hugeicons/core-free-icons'
import { useQuery } from '@tanstack/react-query'
import { useEffect, useState } from 'react'

import { Button } from '@/components/ui/button'
import { Link } from '@/components/ui/link'
import { useTranscriptionEngine } from '@/domain/model-providers/apis/transcription-engine'
import { sessionQueryOptions } from '@/domain/sessions/apis/get-session'
import { plainPreviewOf } from '@/domain/sessions/editor/content'
import { focusEditorIn } from '@/domain/sessions/editor/focus-editor'
import { cn } from '@/lib/utils'

import type { CaptureKind, CaptureView } from '../capture-view'
import { httpsGuideUrl } from '../https-guide'
import { useCapture, type Capture } from '../use-capture'

const typeInstead = ' You can type your entry now.'

const clock = (ms: number) => {
  const seconds = Math.floor(ms / 1000)
  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`
}

/** The words of each state. A state that holds audio says where the audio is, because that is what the person needs to know. */
function wordsOf(view: CaptureView): { title: string; line: string } {
  switch (view.kind) {
    case 'idle':
      return { title: 'Tap to record', line: 'Your audio saves in this browser first, also with no network. Then it goes to your Recall server for the words.' }
    case 'starting':
      return { title: 'Starting the mic', line: 'If the browser asks, allow the mic.' }
    case 'recording':
      return { title: 'Recording', line: 'Each second saves in this browser as you speak. Tap to stop.' }
    case 'stored':
      return { title: 'Saved on this device', line: `${clock(view.durationMs)} of audio is in this browser. It uploads next.` }
    case 'sending':
      return { title: `Sending ${clock(view.durationMs)} of audio`, line: 'The audio stays in this browser until the server has all of it.' }
    case 'offline':
      return {
        title: 'No network. The upload waits',
        line: `${clock(view.durationMs)} of audio is safe in this browser. It uploads when the network comes back. You can keep recording.`,
      }
    case 'serverDown':
      return {
        title: 'The server does not answer. The upload waits',
        line: `${clock(view.durationMs)} of audio is safe in this browser. It uploads when the server answers again. You can keep recording.`,
      }
    case 'refused':
      return { title: 'The server did not take this audio', line: `${view.text} The audio stays in this browser.` }
    case 'transcribing':
      return { title: 'Writing the words', line: view.problemText ?? 'The server has your audio. You can close this page. The words come to your journal.' }
    case 'ready':
      return { title: 'Your words are in', line: 'A new entry holds the words of this recording.' }
    case 'failed':
      return { title: 'No words came from this audio', line: `${view.problemText ?? 'The transcription failed.'} The audio is safe on the server.` }
    case 'denied':
      return {
        title: 'The browser blocks the mic',
        line: `To allow it, open the settings of this site (the icon at the left of the address, or aA on an iPhone), set Microphone to Allow, then try again.${typeInstead}`,
      }
    case 'noMic':
      return { title: 'No mic found', line: `Connect a mic, then try again.${typeInstead}` }
    case 'micBusy':
      return { title: 'Another app uses the mic', line: `Close the other app that records, then try again.${typeInstead}` }
    case 'micFailed':
      return { title: 'The mic did not start', line: `Try again.${typeInstead}` }
    case 'unsupported':
      return { title: 'This browser cannot record audio', line: `Use a recent Chrome, Edge, Firefox, or Safari to record.${typeInstead}` }
    case 'insecure':
      return { title: 'The mic needs HTTPS', line: `This page opened on plain HTTP, so the mic stays off.${typeInstead}` }
    case 'storeFailed':
      return {
        title: 'This browser has no room for more audio',
        line: 'The recording stopped. The audio before the stop is safe and uploads. Free some space on this device, then record again.',
      }
  }
}

const iconOf: Partial<Record<CaptureKind, typeof Mic01Icon>> = {
  stored: HardDriveIcon,
  sending: CloudUploadIcon,
  transcribing: Loading03Icon,
  ready: CheckmarkCircle02Icon,
  offline: WifiDisconnected01Icon,
  serverDown: WifiDisconnected01Icon,
  refused: AlertCircleIcon,
  failed: AlertCircleIcon,
  storeFailed: AlertCircleIcon,
}

const isProblem = (kind: CaptureKind) =>
  kind === 'denied' || kind === 'insecure' || kind === 'failed' || kind === 'refused' || kind === 'noMic' || kind === 'micBusy' || kind === 'micFailed' || kind === 'unsupported' || kind === 'storeFailed'

const isMicOff = (kind: CaptureKind) => kind === 'insecure' || kind === 'unsupported' || kind === 'denied'

function typeAnEntry() {
  focusEditorIn(document.querySelector<HTMLElement>('section[aria-label="Write a new entry"]'))
}

function RecordingClock({ startedAt }: { startedAt: number }) {
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 250)
    return () => clearInterval(timer)
  }, [])
  return <p className="text-4xl font-semibold tabular-nums">{clock(Math.max(0, now - startedAt))}</p>
}

const barCount = 24

function LevelMeter({ level }: { level: () => number }) {
  const [levels, setLevels] = useState<number[]>(() => Array.from({ length: barCount }, () => 0))
  useEffect(() => {
    const timer = setInterval(() => setLevels((previous) => [...previous.slice(1), level()]), 90)
    return () => clearInterval(timer)
  }, [level])
  return (
    <div aria-hidden className="flex h-8 items-center gap-[3px] text-destructive">
      {levels.map((value, index) => (
        <span key={index} className="w-[3px] rounded-full bg-current" style={{ height: `${Math.max(12, value * 100)}%` }} />
      ))}
    </div>
  )
}

function SendProgress({ sent, total }: { sent: number; total: number }) {
  return (
    <div
      role="progressbar"
      aria-label="Upload"
      aria-valuemin={0}
      aria-valuemax={total}
      aria-valuenow={sent}
      className="h-1.5 w-full max-w-xs overflow-hidden rounded-full bg-muted"
    >
      <div className="h-full rounded-full bg-primary transition-[width] duration-300" style={{ width: `${total === 0 ? 0 : (sent / total) * 100}%` }} />
    </div>
  )
}

function EnginePlace() {
  const engine = useTranscriptionEngine()
  if (!engine.isSuccess) return null
  const place = engine.data.engine === 'Cloud' ? engine.data.modelProviderName : 'This server'
  return (
    <p className="text-xs text-muted-foreground">
      Transcribe on: <span className="wrap-anywhere">{place}</span>.{' '}
      <Link to="/transcription-engine" className="inline-flex min-h-11 items-center font-medium text-primary underline underline-offset-4 sm:min-h-0">
        Change
      </Link>
    </p>
  )
}

function WordsOfTheEntry({ sessionId }: { sessionId: string }) {
  const session = useQuery(sessionQueryOptions(sessionId))
  const text = session.data && plainPreviewOf(session.data.rawText, 400)
  return (
    <figure className="flex w-full max-w-lg flex-col gap-2 rounded-lg bg-muted px-4 py-3 text-left">
      {text && <blockquote className="line-clamp-3 text-sm wrap-anywhere">{text}</blockquote>}
      <figcaption className="flex justify-end">
        <Link to="/sessions/$id" params={{ id: sessionId }} className="inline-flex min-h-11 items-center rounded-lg border bg-background px-3 text-sm font-medium sm:min-h-8">
          Open entry
        </Link>
      </figcaption>
    </figure>
  )
}

function Actions({ view, capture }: { view: CaptureView; capture: Capture }) {
  switch (view.kind) {
    case 'denied':
    case 'noMic':
    case 'micBusy':
    case 'micFailed':
      return (
        <div className="flex flex-wrap justify-center gap-2">
          <Button className="h-11 px-4" onPress={capture.record}>Try again</Button>
          <Button variant="outline" className="h-11 px-4" onPress={typeAnEntry}>Type instead</Button>
        </div>
      )
    case 'unsupported':
      return <Button variant="outline" className="h-11 px-4" onPress={typeAnEntry}>Type instead</Button>
    case 'insecure':
      return (
        <a href={httpsGuideUrl} target="_blank" rel="noreferrer" className="inline-flex min-h-11 items-center font-medium text-primary underline underline-offset-4">
          Turn on HTTPS
        </a>
      )
    case 'failed':
      return (
        <div className="flex flex-col items-center gap-2">
          <div className="flex flex-wrap justify-center gap-2">
            <Button className="h-11 px-4" isPending={capture.isRetrying} onPress={() => capture.retry(view.recordingId)}>Try again</Button>
            <Link to="/sessions/$id" params={{ id: view.sessionId }} className="inline-flex h-11 items-center rounded-lg border bg-background px-4 text-sm font-medium">
              Open entry
            </Link>
          </div>
          {capture.retryError && <p className="text-sm text-destructive">The server did not start the transcription again. Try again in a moment.</p>}
        </div>
      )
    case 'refused':
      return <Button className="h-11 px-4" onPress={() => capture.sendAgain(view.clientRecordingId)}>Send again</Button>
    case 'ready':
      return <WordsOfTheEntry sessionId={view.sessionId} />
    case 'sending':
      return <SendProgress sent={view.sentSlices} total={view.totalSlices} />
    default:
      return null
  }
}

/** The mic of the journal home. One tap records into a new entry, and the card follows the audio until its words are in the journal. */
export function MicCard() {
  const capture = useCapture()
  const { view } = capture
  const isRecording = view.kind === 'recording'
  const isOff = isMicOff(view.kind)
  const words = wordsOf(view)
  const icon = iconOf[view.kind]

  return (
    <section
      aria-label="Record an entry"
      className={cn('flex flex-col items-center gap-4 rounded-2xl border bg-card px-4 py-6 text-center md:py-8', isProblem(view.kind) && 'border-destructive/30')}
    >
      <button
        type="button"
        disabled={isOff || view.kind === 'starting' || !capture.isLoaded}
        aria-label={isRecording ? 'Stop recording' : 'Record'}
        onClick={isRecording ? capture.stop : capture.record}
        className={cn(
          'relative grid size-24 place-items-center rounded-full shadow-lg transition-transform outline-none focus-visible:ring-4 focus-visible:ring-ring/50 active:scale-95 md:size-28',
          isRecording ? 'bg-destructive text-white' : 'bg-primary text-primary-foreground',
          (isOff || view.kind === 'starting') && 'bg-muted text-muted-foreground shadow-none',
        )}
      >
        {isRecording && <span aria-hidden className="absolute inset-0 animate-ping rounded-full bg-destructive/40 [animation-duration:1.6s]" />}
        <HugeiconsIcon icon={isRecording ? StopIcon : view.kind === 'insecure' ? SquareLock01Icon : isOff ? MicOff01Icon : Mic01Icon} size={40} className="relative" />
      </button>

      {isRecording && (
        <div aria-hidden className="-mb-2 flex flex-col items-center gap-2">
          <RecordingClock startedAt={view.startedAt} />
          <LevelMeter level={capture.level} />
        </div>
      )}
      <div role="status" className="flex max-w-md flex-col items-center gap-1.5">
        <p className={cn('flex items-center gap-2 text-lg font-semibold', isRecording && 'sr-only', isProblem(view.kind) && 'text-destructive')}>
          {icon && <HugeiconsIcon icon={icon} size={20} aria-hidden className={cn(view.kind === 'transcribing' && 'animate-spin [animation-duration:2s]', view.kind === 'ready' && 'text-emerald-600 dark:text-emerald-400')} />}
          {words.title}
        </p>
        <p className="text-sm text-balance text-muted-foreground">{words.line}</p>
      </div>

      <Actions view={view} capture={capture} />

      {capture.waiting > 0 && (
        <p className="text-xs text-muted-foreground">
          {capture.waiting === 1 ? 'One more recording waits' : `${capture.waiting} more recordings wait`} in this browser to upload.
        </p>
      )}
      {view.kind === 'idle' && capture.isLoaded && <EnginePlace />}
    </section>
  )
}
