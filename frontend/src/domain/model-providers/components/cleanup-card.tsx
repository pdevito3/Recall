import type { SessionDto } from '@/api/generated/models/types/session-dto'
import { QueryError } from '@/components/query-error'
import { RefusedSaveAlert } from '@/components/refused-save-alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { refusalOf } from '@/hooks/use-server-errors'

import {
  pollWhileRunningMs,
  useApplyWaitingCleanup,
  useCleanupSession,
  useRespondToProposal,
  useRespondToSuggestion,
  useStartCleanup,
  useStopCleanup,
} from '../apis/cleanup'
import { useActiveModelProvider } from '../apis/model-providers'
import { plainTextOf } from './plain-text'

const STATUS_TEXT: Record<SessionDto['cleanupStatus'], string> = {
  NotRun: 'No Cleanup ran yet.',
  Running: 'The Cleanup runs. You can close this page, and the result stays.',
  Clean: 'Cleaned is current.',
  Stale: 'Raw changed after the last Cleanup.',
  Failed: 'The last Cleanup did not finish. Your words did not change.',
}

const STOPPED_TEXT = 'You stopped the last Cleanup. Your words did not change, and you can run it again.'

const WAITING_TEXT = 'The last Cleanup ended, and Cleaned kept your words.'

const NO_PROVIDER_TEXT = 'An Admin must connect a model provider, pick a model, and make it active first.'

const FAILURE_TEXT: Record<SessionDto['cleanupFailure'], string | null> = {
  None: null,
  NoModelProvider: NO_PROVIDER_TEXT,
  KeyUnreadable: 'The server cannot read the API key of the model provider. An Admin must enter the key again.',
  ProviderRefused: 'The model provider refused the request. An Admin can test the connection on this page.',
  ProviderUnavailable: 'The model provider did not answer. Try again later.',
  BadModelOutput: 'The answer of the model is not one that the app can use. Try again.',
  Interrupted: 'The Cleanup stopped before it got a result. Try again.',
  Stopped: 'You stopped the Cleanup. You can run it again.',
}

function Words({ title, text }: { title: string; text: string }) {
  return (
    <section className="flex flex-col gap-1">
      <h3 className="text-sm font-medium">{title}</h3>
      <p className="whitespace-pre-wrap wrap-anywhere rounded-lg border p-3 text-sm">{plainTextOf(text) || 'No words yet.'}</p>
    </section>
  )
}

function Suggestions({ session }: { session: SessionDto }) {
  const respond = useRespondToSuggestion(session.id)
  if (session.suggestions.length === 0) return null
  return (
    <section className="flex flex-col gap-2">
      <h3 className="text-sm font-medium">Suggestions</h3>
      <ul className="flex flex-col gap-2">
        {session.suggestions.map(({ kind, value }) => (
          <li
            key={`${kind}-${value}`}
            className="grid grid-cols-[auto_1fr_auto] items-center gap-x-2 gap-y-1 text-sm max-sm:grid-cols-[auto_1fr]"
          >
            <Badge variant="outline" className="max-sm:row-start-2">
              {kind}
            </Badge>
            <span className="min-w-0 wrap-anywhere max-sm:col-span-2 max-sm:row-start-1">{value}</span>
            <div className="flex items-center gap-2 justify-self-end max-sm:row-start-2">
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

function PeopleProposals({ session }: { session: SessionDto }) {
  const respond = useRespondToProposal(session.id)
  if (session.peopleProposals.length === 0) return null
  return (
    <section className="flex flex-col gap-2">
      <h3 className="text-sm font-medium">People</h3>
      <ul className="flex flex-col gap-2">
        {session.peopleProposals.map(({ label }) => (
          <li key={label} className="flex flex-wrap items-center gap-2 text-sm">
            {respond.error && respond.variables?.label === label && (
              <div className="basis-full">
                <RefusedSaveAlert problem={refusalOf(respond.error, []).problem} />
              </div>
            )}
            <span className="min-w-24 flex-1 basis-48 wrap-anywhere">{label}</span>
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

export function CleanupCard({ sessionId }: { sessionId: string }) {
  const session = useCleanupSession(sessionId)
  const start = useStartCleanup(sessionId)
  const stop = useStopCleanup(sessionId)
  const apply = useApplyWaitingCleanup(sessionId)
  // Each try of a run reads the active model provider again, so the line follows a change while the run lasts.
  const provider = useActiveModelProvider(session.data?.cleanupStatus === 'Running' ? pollWhileRunningMs : false)
  const startProblem = start.error ? refusalOf(start.error, []).problem : null
  const stopProblem = stop.error ? refusalOf(stop.error, []).problem : null
  const applyProblem = apply.error ? refusalOf(apply.error, []).problem : null

  if (session.isPending) {
    return (
      <p role="status" aria-label="Loading Session">
        Loading Session
      </p>
    )
  }
  if (session.isError) {
    return <QueryError title="The Session did not load." error={session.error} onRetry={() => void session.refetch()} />
  }

  const { data } = session
  const isRunning = data.cleanupStatus === 'Running'
  const isStop = data.cleanupStatus === 'Failed' && data.cleanupFailure === 'Stopped'
  const failure = isStop ? null : FAILURE_TEXT[data.cleanupFailure]
  const hasNoProvider = provider.isSuccess && !provider.data.name
  const waiting = isRunning ? null : (data.waitingCleanupRevision ?? null)
  const cleanedMovedInRun = isRunning && data.cleanedDraftRevision !== data.cleanupBaseCleanedRevision
  const handEditNote = cleanedMovedInRun
    ? 'Cleaned changed during this Cleanup. Cleaned keeps your words, and the result will wait in History.'
    : data.cleanedHasHandEdits && waiting === null
      ? `You edited Cleaned by hand. ${isRunning ? 'This' : 'A new'} Cleanup replaces Cleaned, and your edit stays as a Revision.`
      : null
  const statusText = isStop ? STOPPED_TEXT : waiting !== null ? WAITING_TEXT : STATUS_TEXT[data.cleanupStatus]

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-lg">
          <h2>Cleanup</h2>
        </CardTitle>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
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
              Cleaned changed during the last Cleanup, so the result waits in History as Revision {waiting}. Use it to replace
              Cleaned, and your words stay as a Revision.
            </p>
            <Button
              variant="outline"
              className="self-start"
              isPending={apply.isPending}
              onPress={() => apply.mutate({ revision: waiting, baseCleanedRevision: data.cleanedDraftRevision })}
            >
              Use the Cleanup result
            </Button>
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
          <Button isPending={start.isPending} isDisabled={isRunning || hasNoProvider} onPress={() => start.mutate()}>
            {data.cleanupStatus === 'NotRun' || isRunning ? 'Run Cleanup' : 'Run Cleanup again'}
          </Button>
          {isRunning && (
            <Button variant="outline" isPending={stop.isPending} onPress={() => stop.mutate()}>
              Stop Cleanup
            </Button>
          )}
        </div>
        {data.synopsis && (
          <section className="flex flex-col gap-1">
            <h3 className="text-sm font-medium">Synopsis</h3>
            <p className="text-sm wrap-anywhere">{data.synopsis}</p>
          </section>
        )}
        <Words title="Cleaned" text={data.cleanedText} />
        <Words title="Raw" text={data.rawText} />
        <Suggestions session={data} />
        <PeopleProposals key={data.cleanedLatestRevision} session={data} />
      </CardContent>
    </Card>
  )
}
