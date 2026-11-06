import type { SessionDto } from '@/api/generated/models/types/session-dto'
import { QueryError } from '@/components/query-error'
import { RefusedSaveAlert } from '@/components/refused-save-alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { refusalOf } from '@/hooks/use-server-errors'

import { useCleanupSession, useRespondToProposal, useRespondToSuggestion, useStartCleanup } from '../apis/cleanup'
import { plainTextOf } from './plain-text'

const STATUS_TEXT: Record<SessionDto['cleanupStatus'], string> = {
  NotRun: 'No Cleanup ran yet.',
  Running: 'The Cleanup runs. You can close this page, and the result stays.',
  Clean: 'Cleaned is current.',
  Stale: 'Raw changed after the last Cleanup.',
  Failed: 'The last Cleanup did not finish. Your words did not change.',
}

const FAILURE_TEXT: Record<SessionDto['cleanupFailure'], string | null> = {
  None: null,
  NoModelProvider: 'An Admin must connect a model provider and pick a model first.',
  KeyUnreadable: 'The server cannot read the API key of the model provider. An Admin must enter the key again.',
  ProviderRefused: 'The model provider refused the request. An Admin can test the connection on this page.',
  ProviderUnavailable: 'The model provider did not answer after several tries. Try again later.',
  BadModelOutput: 'The answer of the model is not one that the app can use. Try again.',
  Interrupted: 'The Cleanup stopped before it got a result. Try again.',
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
          <li key={`${kind}-${value}`} className="flex flex-wrap items-center gap-2 text-sm">
            <Badge variant="outline">{kind}</Badge>
            <span className="min-w-0 flex-1 wrap-anywhere">{value}</span>
            <Button size="sm" variant="outline" aria-label={`Accept ${value}`} onPress={() => respond.mutate({ accept: true, kind, value })}>
              Accept
            </Button>
            <Button size="sm" variant="ghost" aria-label={`Reject ${value}`} onPress={() => respond.mutate({ accept: false, kind, value })}>
              Reject
            </Button>
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
        {session.peopleProposals.map(({ label, isNew }) => (
          <li key={label} className="flex flex-wrap items-center gap-2 text-sm">
            <span className="min-w-0 flex-1 wrap-anywhere">
              {label} {isNew && <span className="text-muted-foreground">(new person)</span>}
            </span>
            <Button
              size="sm"
              variant="outline"
              aria-label={`Tag ${label}`}
              onPress={() => respond.mutate({ label, approve: true, createNew: isNew })}
            >
              Tag
            </Button>
            <Button size="sm" variant="ghost" aria-label={`Do not tag ${label}`} onPress={() => respond.mutate({ label, approve: false, createNew: false })}>
              Do not tag
            </Button>
          </li>
        ))}
      </ul>
    </section>
  )
}

export function CleanupCard({ sessionId }: { sessionId: string }) {
  const session = useCleanupSession(sessionId)
  const start = useStartCleanup(sessionId)
  const startProblem = start.error ? refusalOf(start.error, []).problem : null

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
  const failure = data.cleanupStatus === 'Failed' ? FAILURE_TEXT[data.cleanupFailure] : null

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-lg">
          <h2>Cleanup</h2>
        </CardTitle>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        <p role="status" className="text-sm">
          {STATUS_TEXT[data.cleanupStatus]}
        </p>
        {failure && (
          <p role="alert" className="text-sm text-destructive">
            {failure}
          </p>
        )}
        {data.cleanedHasHandEdits && !isRunning && (
          <p className="text-sm text-muted-foreground">
            You edited Cleaned by hand. A new Cleanup replaces Cleaned, and your edit stays as a Revision.
          </p>
        )}
        <RefusedSaveAlert problem={startProblem} />
        <div>
          <Button isPending={start.isPending} isDisabled={isRunning} onPress={() => start.mutate()}>
            {data.cleanupStatus === 'NotRun' ? 'Run Cleanup' : 'Run Cleanup again'}
          </Button>
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
        <PeopleProposals session={data} />
      </CardContent>
    </Card>
  )
}
