import { useEffect, useRef } from 'react'

import type { SessionDto } from '@/api/generated/models/types/session-dto'
import { QueryError } from '@/components/query-error'
import { RefusedSaveAlert } from '@/components/refused-save-alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { useActiveModelProvider } from '@/domain/model-providers/apis/model-providers'
import { refusalOf } from '@/hooks/use-server-errors'

import {
  useApplyWaitingCleanup,
  useRespondToProposal,
  useRespondToSuggestion,
  useStartCleanup,
  useStopCleanup,
  useWaitingCleanupText,
} from '../apis/cleanup'
import { pollWhileRunningMs } from '../apis/get-session'
import { mentionLabelsOf } from '../editor/content'

const STATUS_TEXT: Record<SessionDto['cleanupStatus'], string> = {
  NotRun: 'No Cleanup ran yet.',
  Running: 'The Cleanup runs. You can close this page, and the result stays.',
  Clean: 'Cleaned is current.',
  Stale: 'Your words changed after the last Cleanup.',
  Failed: 'The last Cleanup did not finish. Your words did not change.',
}

const STOPPED_TEXT = 'You stopped the last Cleanup. Your words did not change, and you can run it again.'

const WAITING_TEXT = 'The last Cleanup ended, and Cleaned kept your words.'

const HAND_EDITED_TEXT = 'You edited Cleaned by hand after the last Cleanup.'

const names = new Intl.ListFormat('en', { type: 'conjunction' })

const NO_PROVIDER_TEXT = 'An Admin must connect a model provider, pick a model, and make it active first.'

const FAILURE_TEXT: Record<SessionDto['cleanupFailure'], string | null> = {
  None: null,
  NoModelProvider: NO_PROVIDER_TEXT,
  KeyUnreadable: 'The server cannot read the API key of the model provider. An Admin must enter the key again.',
  ProviderRefused: 'The model provider refused the request. An Admin can test the connection on the Model providers page.',
  ProviderUnavailable: 'The model provider did not answer. Try again later.',
  BadModelOutput: 'The answer of the model is not one that the app can use. Try again.',
  Interrupted: 'The Cleanup stopped before it got a result. Try again.',
  Stopped: 'You stopped the Cleanup. You can run it again.',
}

interface CleanupPartProps {
  session: SessionDto
}

function Suggestions({ session }: CleanupPartProps) {
  const respond = useRespondToSuggestion(session.id)
  if (session.suggestions.length === 0) return null
  return (
    <section className="flex flex-col gap-2">
      <h3 className="text-sm font-medium">Suggestions</h3>
      <ul className="flex flex-col gap-2">
        {session.suggestions.map(({ kind, value }) => (
          <li key={`${kind}-${value}`} className="flex flex-wrap items-center gap-2 text-sm">
            <span className="flex min-w-24 flex-1 basis-48 items-center gap-2">
              <Badge variant="outline" className="shrink-0">{kind}</Badge>
              <span dir="auto" className="min-w-0 wrap-anywhere">{value}</span>
            </span>
            <div className="flex items-center gap-2">
              <Button size="sm" variant="outline" aria-label={`Accept ${value}`} onPress={() => respond.mutate({ accept: true, kind, value })}>
                Accept
              </Button>
              <Button size="sm" variant="ghost" aria-label={`Reject ${value}`} onPress={() => respond.mutate({ accept: false, kind, value })}>
                Reject
              </Button>
            </div>
          </li>
        ))}
      </ul>
    </section>
  )
}

function PeopleProposals({ session }: CleanupPartProps) {
  const respond = useRespondToProposal(session.id)
  if (session.peopleProposals.length === 0) return null
  return (
    <section className="flex flex-col gap-2">
      <h3 className="text-sm font-medium">People to tag</h3>
      <ul className="flex flex-col gap-2">
        {session.peopleProposals.map(({ label }) => (
          <li key={label} className="flex flex-wrap items-center gap-2 text-sm">
            {respond.error && respond.variables?.label === label && (
              <div className="basis-full">
                <RefusedSaveAlert problem={refusalOf(respond.error, []).problem} />
              </div>
            )}
            <span dir="auto" className="min-w-24 flex-1 basis-48 wrap-anywhere">{label}</span>
            <div className="flex items-center gap-2">
              <Button size="sm" variant="outline" aria-label={`Tag ${label}`} onPress={() => respond.mutate({ label, approve: true, createNew: false })}>
                Tag
              </Button>
              <Button size="sm" variant="ghost" aria-label={`Do not tag ${label}`} onPress={() => respond.mutate({ label, approve: false, createNew: false })}>
                Do not tag
              </Button>
            </div>
          </li>
        ))}
      </ul>
    </section>
  )
}

/** The Cleanup of one entry. Its words show in the Cleaned tab of the editor, and an accepted suggestion shows in the tags. */
export function CleanupCard({ session }: CleanupPartProps) {
  const start = useStartCleanup(session.id)
  const stop = useStopCleanup(session.id)
  const apply = useApplyWaitingCleanup(session.id)
  const isRunning = session.cleanupStatus === 'Running'
  const waiting = isRunning ? null : (session.waitingCleanupRevision ?? null)
  const waitingText = useWaitingCleanupText(session.id, waiting)
  // Each try of a run reads the active model provider again, so the line follows a change while the run lasts.
  const provider = useActiveModelProvider(isRunning ? pollWhileRunningMs : false)
  const startProblem = start.error ? refusalOf(start.error, []).problem : null
  const stopProblem = stop.error ? refusalOf(stop.error, []).problem : null
  const applyProblem = apply.error ? refusalOf(apply.error, []).problem : null
  const { reset: resetApply } = apply

  // A use that a run refused says to look again when the run ends, so the refusal ends with the run.
  useEffect(() => {
    if (!isRunning) resetApply()
  }, [isRunning, resetApply])

  // A run turns Run Cleanup off, and its end removes Stop Cleanup. The focus goes to the other button, so it does not fall to the page.
  const runRef = useRef<HTMLButtonElement>(null)
  const stopRef = useRef<HTMLButtonElement>(null)
  const lastFocused = useRef<'run' | 'stop' | null>(null)
  useEffect(() => {
    const target = isRunning && lastFocused.current === 'run' ? stopRef.current : !isRunning && lastFocused.current === 'stop' ? runRef.current : null
    const focused = document.activeElement
    if (target && (focused === null || focused === document.body || focused === runRef.current)) target.focus()
  }, [isRunning])

  const isStop = session.cleanupStatus === 'Failed' && session.cleanupFailure === 'Stopped'
  const failure = isStop ? null : FAILURE_TEXT[session.cleanupFailure]
  const hasNoProvider = provider.isSuccess && !provider.data.name
  const isClean = session.cleanupStatus === 'Clean'
  const saysHandEdit = isClean && waiting === null && session.cleanedHasHandEdits
  const cleanedMovedInRun = isRunning && session.cleanedDraftRevision !== session.cleanupBaseCleanedRevision
  const handEditNote = cleanedMovedInRun
    ? 'Cleaned changed during this Cleanup. Cleaned keeps your words, and the result will wait in History.'
    : session.cleanedHasHandEdits && waiting === null
      ? `${saysHandEdit ? '' : 'You edited Cleaned by hand. '}${isRunning ? 'This' : 'A new'} Cleanup replaces Cleaned, and your edit stays as a Revision.`
      : null
  const statusText = isStop
    ? STOPPED_TEXT
    : isClean && waiting !== null
      ? WAITING_TEXT
      : saysHandEdit
        ? HAND_EDITED_TEXT
        : STATUS_TEXT[session.cleanupStatus]
  const resultTags = waitingText.data === undefined ? null : mentionLabelsOf(waitingText.data)
  const droppedTags = resultTags
    ? [...mentionLabelsOf(session.cleanedText)].filter(([personId]) => !resultTags.has(personId)).map(([, label]) => label)
    : []

  return (
    <section aria-labelledby="entry-cleanup" className="flex flex-col gap-3">
      <h2 id="entry-cleanup" className="text-lg font-semibold">Cleanup</h2>
      <p role="status" className="text-sm">
        {statusText}
      </p>
      {failure && (
        <p role="alert" className="text-sm text-destructive">
          {failure}
        </p>
      )}
      {handEditNote && <p className="text-sm text-muted-foreground">{handEditNote}</p>}
      {waiting !== null && (
        <div className="flex flex-col gap-2 rounded-lg border p-3 text-sm">
          <p>
            A Cleanup result waits in History as Revision {waiting}, because Cleaned changed during its run. Use it to replace
            Cleaned, and your words stay as a Revision.
          </p>
          {droppedTags.length > 0 && (
            <p className="wrap-anywhere">
              Cleaned tags {names.format(droppedTags)}, and this result does not. If you use it, Cleaned loses{' '}
              {droppedTags.length === 1 ? 'that tag' : 'those tags'}.
            </p>
          )}
          <Button
            variant="outline"
            className="self-start"
            isPending={apply.isPending}
            isDisabled={!waitingText.isSuccess}
            onPress={() => apply.mutate({ revision: waiting, baseCleanedRevision: session.cleanedDraftRevision })}
          >
            Use the Cleanup result
          </Button>
          {waitingText.isError && (
            <QueryError title="The result did not load, so the page cannot say which tags a use keeps." error={waitingText.error} onRetry={() => void waitingText.refetch()} />
          )}
        </div>
      )}
      {provider.data?.name && (
        <p className="text-sm text-muted-foreground wrap-anywhere">
          {isRunning ? 'This Cleanup sends your words to' : 'A Cleanup sends your words to'} {provider.data.name}.
        </p>
      )}
      {hasNoProvider && failure !== NO_PROVIDER_TEXT && <p className="text-sm text-muted-foreground">{NO_PROVIDER_TEXT}</p>}
      <RefusedSaveAlert problem={startProblem ?? stopProblem ?? applyProblem} />
      <div className="flex flex-wrap gap-2">
        <Button
          ref={runRef}
          isPending={start.isPending}
          isDisabled={isRunning || hasNoProvider}
          onFocus={() => {
            lastFocused.current = 'run'
          }}
          onPress={() => {
            apply.reset()
            start.mutate()
          }}
        >
          {session.cleanupStatus === 'NotRun' || isRunning ? 'Run Cleanup' : 'Run Cleanup again'}
        </Button>
        {isRunning && (
          <Button
            ref={stopRef}
            variant="outline"
            isPending={stop.isPending}
            onFocus={() => {
              lastFocused.current = 'stop'
            }}
            onPress={() => stop.mutate()}
          >
            Stop Cleanup
          </Button>
        )}
      </div>
      <Suggestions session={session} />
      <PeopleProposals key={session.cleanedLatestRevision} session={session} />
    </section>
  )
}
