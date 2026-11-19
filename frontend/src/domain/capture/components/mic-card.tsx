import { HugeiconsIcon } from '@hugeicons/react'
import {
  AlertCircleIcon,
  CheckmarkCircle02Icon,
  CloudUploadIcon,
  HardDriveIcon,
  Loading03Icon,
  Mic01Icon,
  MicOff01Icon,
  ParagraphIcon,
  SquareLock01Icon,
  StopIcon,
  WifiDisconnected01Icon,
} from '@hugeicons/core-free-icons'
import { useQuery } from '@tanstack/react-query'
import { useEffect, useLayoutEffect, useRef, useState, type RefObject } from 'react'

import { Button } from '@/components/ui/button'
import { buttonVariants } from '@/components/ui/button-variants'
import { Link } from '@/components/ui/link'
import { Skeleton } from '@/components/ui/skeleton'
import { useTranscriptionEngine } from '@/domain/model-providers/apis/transcription-engine'
import { sessionQueryOptions } from '@/domain/sessions/apis/get-session'
import { useJournalZone } from '@/domain/settings/apis/user-setting'
import { plainPreviewOf } from '@/domain/sessions/editor/content'
import { focusEditorIn } from '@/domain/sessions/editor/focus-editor'
import { cn } from '@/lib/utils'

import { clock, timeOf } from '../audio-time'
import type { CaptureKind, CaptureView } from '../capture-view'
import { httpsGuideUrl } from '../https-guide'
import { useCapture, type Capture } from '../use-capture'

const typeInstead = ' You can type your entry now.'

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
      return view.isOnServer
        ? {
            title: 'No network. The words wait',
            line: `The server has all ${clock(view.durationMs)} of this audio. The words start when the network comes back, or on their own about a day later. You can keep recording.`,
          }
        : {
            title: 'No network. The upload waits',
            line: `${clock(view.durationMs)} of audio is safe in this browser. It uploads when the network comes back. You can keep recording.`,
          }
    case 'serverDown':
      return view.isOnServer
        ? {
            title: 'The server does not answer. The words wait',
            line: `The server has all ${clock(view.durationMs)} of this audio. The words start when the server answers again, or on their own about a day later. You can keep recording.`,
          }
        : {
            title: 'The server does not answer. The upload waits',
            line: `${clock(view.durationMs)} of audio is safe in this browser. It uploads when the server answers again. You can keep recording.`,
          }
    case 'refused':
      return { title: 'The server did not take this audio', line: `${view.text} The audio stays in this browser.` }
    case 'transcribing':
      return { title: 'Writing the words', line: view.problemText ?? 'The server has your audio. You can close this page. The words come to your journal.' }
    case 'ready':
      return view.problemText
        ? { title: 'Your words are in, with a gap', line: view.problemText }
        : { title: 'Your words are in', line: 'A new entry holds the words of this recording.' }
    case 'failed':
      // The text of the server says where the audio is, because only the server knows: it can also have lost the audio.
      return { title: 'No words came from this audio', line: view.problemText ?? 'The transcription failed.' }
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
    case 'noStore':
      return {
        title: 'This browser does not keep audio',
        line: `Recall saves each second in the browser before the upload, and this browser refuses that, as some private windows do. Open Recall in a normal window to record.${typeInstead}`,
      }
    case 'insecure':
      return { title: 'The mic needs HTTPS', line: `This page opened on plain HTTP, so the mic stays off.${typeInstead}` }
    case 'storeFailed':
      return {
        title: 'This browser has no room for more audio',
        line: 'The recording stopped. The audio before the stop is safe and uploads. Free some space on this device, then record again.',
      }
    case 'entryDeleted':
      return { title: 'The entry was deleted', line: 'The entry of this recording was deleted, so the recording stopped. Tap to record a new entry.' }
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
  kind === 'denied' || kind === 'insecure' || kind === 'failed' || kind === 'refused' || kind === 'noMic' || kind === 'micBusy' || kind === 'micFailed' || kind === 'unsupported' || kind === 'noStore' || kind === 'storeFailed'

const isMicOff = (kind: CaptureKind) => kind === 'insecure' || kind === 'unsupported' || kind === 'noStore' || kind === 'denied'

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

// A narrow card has no room beside the clock, so there the marks cover the oldest bars of the level meter.
function ParagraphMarks({ count }: { count: number }) {
  return (
    <span
      data-testid="paragraph-marks"
      className="absolute start-full top-1/2 ms-3 inline-flex -translate-y-1/2 items-center gap-0.5 rounded-full bg-[color-mix(in_oklab,var(--destructive)_10%,var(--card))] px-2 py-0.5 text-sm font-semibold text-destructive tabular-nums @max-[14rem]:start-0 @max-[14rem]:top-auto @max-[14rem]:bottom-1 @max-[14rem]:ms-0 @max-[14rem]:translate-y-0"
    >
      <span className="absolute inset-0 overflow-hidden rounded-full">
        <span
          key={count}
          className="absolute inset-0 rounded-full bg-destructive/30 motion-safe:animate-ping motion-reduce:hidden"
          style={{ animationIterationCount: 1, animationFillMode: 'forwards' }}
        />
      </span>
      <HugeiconsIcon icon={ParagraphIcon} size={14} />
      {count}
    </span>
  )
}

// The label wraps in a narrow card with a large font, and the room of the idle card wraps with it. In the narrowest card
// only the label fits.
const newParagraphClass =
  'h-auto min-h-12 w-full max-w-xs px-6 py-2 text-base whitespace-normal @max-[10rem]:px-2 @max-[10rem]:[&>svg]:hidden @2xl:col-start-3 @2xl:row-start-1 @2xl:w-auto @2xl:justify-self-start'

/** The time of a tap is the finger down, not the lift, which comes later in the audio. A press that slides off marks nothing. */
function NewParagraphButton({ capture, focusWhenGone }: { capture: Capture; focusWhenGone: RefObject<HTMLButtonElement | null> }) {
  const pressedAt = useRef(0)
  const button = useRef<HTMLButtonElement>(null)
  // The button goes when the recording ends. Not a `useEffect`: its cleanup runs after the button left the DOM, so the
  // focus would already be on the page body.
  useLayoutEffect(() => {
    const element = button.current
    const target = focusWhenGone.current
    return () => {
      if (element && document.activeElement === element) target?.focus()
    }
  }, [focusWhenGone])
  const handlePress = async () => {
    // Android has a vibration. iOS Safari has none, so the mark by the clock and the status say it there.
    if (await capture.markParagraph(pressedAt.current)) navigator.vibrate?.(15)
  }
  return (
    <Button ref={button} variant="outline" className={newParagraphClass} onPressStart={() => (pressedAt.current = performance.now())} onPress={() => void handlePress()}>
      <HugeiconsIcon icon={ParagraphIcon} size={20} aria-hidden />
      New paragraph
    </Button>
  )
}

const barCount = 24

function LevelMeter({ level }: { level: () => number }) {
  const [levels, setLevels] = useState<number[]>(() => Array.from({ length: barCount }, () => 0))
  useEffect(() => {
    const timer = setInterval(() => setLevels((previous) => [...previous.slice(1), level()]), 90)
    return () => clearInterval(timer)
  }, [level])
  return (
    <div aria-hidden className="flex h-8 max-w-full items-center justify-end gap-[0.1875rem] overflow-hidden text-destructive">
      {levels.map((value, index) => (
        <span key={index} className="w-[0.1875rem] shrink-0 rounded-full bg-current" style={{ height: `${Math.max(12, value * 100)}%` }} />
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
      <Link to="/transcription-engine" className="inline-flex min-h-11 min-w-11 items-center justify-center font-medium text-primary underline underline-offset-4 sm:min-h-0 sm:min-w-0">
        Change
      </Link>
    </p>
  )
}

function WordsOfTheEntry({ sessionId }: { sessionId: string }) {
  const session = useQuery(sessionQueryOptions(sessionId))
  const text = session.data && plainPreviewOf(session.data.rawText, 400)
  return (
    <div className="flex w-full max-w-lg flex-col gap-2 rounded-lg bg-muted px-4 py-3 text-left">
      {session.isPending ? (
        <div aria-hidden className="flex flex-col gap-2 py-0.5">
          <Skeleton className="h-4 w-full bg-foreground/10" />
          <Skeleton className="h-4 w-2/3 bg-foreground/10" />
        </div>
      ) : (
        text && <p className="line-clamp-3 text-sm wrap-anywhere">{text}</p>
      )}
      <div className="flex justify-end">
        <Link to="/sessions/$id" params={{ id: sessionId }} className="inline-flex min-h-11 items-center rounded-lg border bg-background px-3 text-sm font-medium sm:min-h-8">
          Open entry
        </Link>
      </div>
    </div>
  )
}

/** A discard loses audio for good, so a second tap confirms it. */
function DiscardAudio({ onDiscard, name }: { onDiscard: () => void; name?: string }) {
  const [isAsking, setIsAsking] = useState(false)
  if (!isAsking) {
    return (
      <Button variant="outline" className="h-11 px-4" aria-label={name && `Discard ${name}`} onPress={() => setIsAsking(true)}>
        Discard
      </Button>
    )
  }
  return (
    <div role="group" aria-label={name ? `Discard ${name}` : 'Discard this audio'} className="flex basis-full flex-col items-center gap-2">
      <span className="text-sm">This audio goes for good.</span>
      <div className="flex flex-wrap justify-center gap-2">
        <Button variant="destructive" className="h-11 px-4" onPress={onDiscard}>
          Discard audio
        </Button>
        <Button variant="outline" className="h-11 px-4" autoFocus onPress={() => setIsAsking(false)}>
          Keep
        </Button>
      </div>
    </div>
  )
}

/** The other recordings that still hold audio in this browser. The queue sends each one by itself, and a refused one waits for the person. */
function WaitingCaptures({ capture }: { capture: Capture }) {
  const { waiting } = capture
  const zone = useJournalZone()
  return (
    <div className="flex w-full max-w-md flex-col gap-2 border-t pt-3">
      <p className="text-xs text-muted-foreground">
        {waiting.length === 1 ? 'One more recording is' : `${waiting.length} more recordings are`} in this browser.
      </p>
      <ul aria-label="Other recordings in this browser" className="flex flex-col gap-2">
        {waiting.map((other) => {
          const name = `the ${clock(other.durationMs)} recording from ${timeOf(other.startedAt, zone)}`
          return (
            <li key={other.clientRecordingId} className="flex flex-col items-center gap-2 rounded-lg border px-3 py-2 text-sm">
              <p className="wrap-anywhere">
                {clock(other.durationMs)} of audio from {timeOf(other.startedAt, zone)}.{' '}
                {other.problem?.kind === 'refused' ? (
                  <span className="text-destructive">The server did not take it: {other.problem.text}</span>
                ) : (
                  'It uploads next.'
                )}
              </p>
              <div className="flex flex-wrap justify-center gap-2">
                {other.problem?.kind === 'refused' && other.problem.canSendAgain !== false && (
                  <Button className="h-11 px-4" aria-label={`Send again ${name}`} onPress={() => capture.sendAgain(other.clientRecordingId)}>
                    Send again
                  </Button>
                )}
                <DiscardAudio name={name} onDiscard={() => capture.discard(other.clientRecordingId)} />
              </div>
            </li>
          )
        })}
      </ul>
    </div>
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
    case 'noStore':
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
      return (
        <div className="flex flex-wrap justify-center gap-2">
          {view.canSendAgain && <Button className="h-11 px-4" onPress={() => capture.sendAgain(view.clientRecordingId)}>Send again</Button>}
          <DiscardAudio key={view.clientRecordingId} onDiscard={() => capture.discard(view.clientRecordingId)} />
        </div>
      )
    case 'ready':
      return <WordsOfTheEntry sessionId={view.sessionId} />
    case 'sending':
      return <SendProgress sent={view.sentSlices} total={view.totalSlices} />
    default:
      return null
  }
}

const clockClass = 'relative -mb-2 flex max-w-full flex-col items-center gap-2 @2xl:col-start-2'
const statusClass = 'flex max-w-md flex-col items-center gap-1.5 @2xl:col-span-full'
// On a wide card New paragraph sits beside the clock, so the recording card is no taller than the idle card and the
// journal home fits a laptop screen. The marks of the clock reach into the gap.
const recordingClass = '@2xl:grid @2xl:grid-cols-[minmax(0,1fr)_9rem_minmax(0,1fr)] @2xl:items-center @2xl:justify-items-center @2xl:gap-x-16'
const titleClass = 'text-lg font-semibold text-balance'
const lineClass = 'text-sm text-balance text-muted-foreground'

/** An unseen copy of the idle words and the engine line while the mic records, or of the clock and the recording words while it does not. */
function RoomOfTheOtherMicState({ isRecording }: { isRecording: boolean }) {
  return (
    <div aria-hidden className={cn('invisible flex w-full flex-col items-center gap-4 [grid-area:1/1]', !isRecording && recordingClass)}>
      {isRecording ? (
        <>
          <div className={statusClass}>
            <p className={titleClass}>{wordsOf({ kind: 'idle' }).title}</p>
            <p className={lineClass}>{wordsOf({ kind: 'idle' }).line}</p>
          </div>
          <EnginePlace />
        </>
      ) : (
        <>
          <div className={clockClass}>
            <p className="text-4xl font-semibold tabular-nums">{clock(0)}</p>
            <div className="h-8" />
          </div>
          <div className={statusClass}>
            <p className={lineClass}>{wordsOf({ kind: 'recording', startedAt: 0 }).line}</p>
          </div>
          <div className={buttonVariants({ variant: 'outline', className: newParagraphClass })}>
            <HugeiconsIcon icon={ParagraphIcon} size={20} />
            New paragraph
          </div>
        </>
      )}
    </div>
  )
}

/** The mic of the journal home. One tap records into a new entry, and the card follows the audio until its words are in the journal. */
export function MicCard() {
  const capture = useCapture()
  const { view } = capture
  const isRecording = view.kind === 'recording'
  const isStarting = view.kind === 'starting'
  const isOff = isMicOff(view.kind)
  const words = wordsOf(view)
  const hasAGap = view.kind === 'ready' && view.problemText !== null
  const icon = hasAGap ? AlertCircleIcon : iconOf[view.kind]
  const isAtTheMic = view.kind === 'idle' || isStarting || isRecording
  const micButton = useRef<HTMLButtonElement>(null)
  const taps = capture.paragraphTapsMs

  return (
    <section
      aria-label="Record an entry"
      className={cn('flex flex-col items-center gap-4 rounded-2xl border bg-card px-4 py-6 text-center md:py-8', isProblem(view.kind) && 'border-destructive/30')}
    >
      {/* While the mic starts, the button stays focusable with aria-disabled: a disabled button drops the focus to the page. */}
      <button
        ref={micButton}
        type="button"
        disabled={isOff || !capture.isLoaded}
        aria-disabled={view.kind === 'starting' || undefined}
        aria-label={isRecording ? 'Stop recording' : 'Record'}
        onClick={view.kind === 'starting' ? undefined : isRecording ? capture.stop : capture.record}
        className={cn(
          'relative grid size-24 place-items-center rounded-full shadow-lg transition-transform outline-none focus-visible:ring-4 focus-visible:ring-ring/50 active:scale-95 md:size-28',
          isRecording ? 'bg-destructive text-destructive-foreground' : 'bg-primary text-primary-foreground',
          (isOff || view.kind === 'starting') && 'bg-muted text-muted-foreground shadow-none',
        )}
      >
        {isRecording && <span aria-hidden className="absolute -inset-2 rounded-full bg-destructive/20 motion-safe:animate-pulse" />}
        <HugeiconsIcon icon={isRecording ? StopIcon : view.kind === 'insecure' ? SquareLock01Icon : isOff ? MicOff01Icon : Mic01Icon} size={40} className="relative" />
      </button>

      {/* The idle card and the recording card share one room, the larger of the two, so the card does not change size under the finger.
          The idle card keeps the room of the clock and of New paragraph. */}
      <div className="@container grid w-full grid-cols-1 justify-items-center">
        <div className={cn('flex w-full flex-col items-center gap-4 [grid-area:1/1]', isRecording && recordingClass)}>
          {isRecording && (
            <div aria-hidden className={clockClass}>
              <div className="relative @max-[14rem]:static">
                <RecordingClock startedAt={view.startedAt} />
                {taps.length > 0 && <ParagraphMarks count={taps.length} />}
              </div>
              <LevelMeter level={capture.level} />
            </div>
          )}
          {/* Until the store answers, the card does not know of stored audio, so it says nothing rather than Tap to record. */}
          <div role="status" data-testid="mic-status" className={cn(statusClass, !capture.isLoaded && !isOff && 'invisible')}>
            {/* The icon flows with the words, so a title that wraps keeps it at the start of its first line. */}
            <p className={cn(titleClass, isRecording && 'sr-only', isProblem(view.kind) && 'text-destructive')}>
              {icon && (
                <HugeiconsIcon
                  icon={icon}
                  size={20}
                  aria-hidden
                  className={cn('me-2 inline-block align-[-3px]', view.kind === 'transcribing' && 'animate-spin [animation-duration:2s]', view.kind === 'ready' && (hasAGap ? 'text-amber-600 dark:text-amber-400' : 'text-emerald-600 dark:text-emerald-400'))}
                />
              )}
              {words.title}
            </p>
            {/* While the mic starts, the line keeps the room of the idle line. */}
            <p className={cn(lineClass, 'grid')}>
              <span className="[grid-area:1/1]">{words.line}</span>
              {isStarting && (
                <span aria-hidden className="invisible [grid-area:1/1]">
                  {wordsOf({ kind: 'idle' }).line}
                </span>
              )}
            </p>
          </div>
          {(view.kind === 'idle' || isStarting) && capture.isLoaded && <EnginePlace />}
          {isRecording && (
            <>
              <NewParagraphButton capture={capture} focusWhenGone={micButton} />
              {/* The clock and its marks are hidden from a screen reader, so this says each tap. A new node each time says it again. */}
              <p role="status" className="sr-only">
                {taps.length > 0 && <span key={taps.length}>New paragraph at {clock(taps[taps.length - 1])}.</span>}
              </p>
            </>
          )}
        </div>
        {isAtTheMic && <RoomOfTheOtherMicState isRecording={isRecording} />}
      </div>

      <Actions view={view} capture={capture} />

      {capture.waiting.length > 0 && <WaitingCaptures capture={capture} />}
    </section>
  )
}
