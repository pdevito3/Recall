import type { ModelProviderListDto } from '@/api/generated/models/types/model-provider-list-dto'
import type { SessionSummaryDto } from '@/api/generated/models/types/session-summary-dto'
import { PageHeading } from '@/components/page-heading'
import { QueryError } from '@/components/query-error'
import { Badge } from '@/components/ui/badge'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Link } from '@/components/ui/link'
import { useMembership } from '@/domain/membership/apis/get-membership'

import { useRecentSessions } from '../apis/cleanup'
import { useModelProviders } from '../apis/model-providers'
import { CleanupCard } from './cleanup-card'
import { ConnectProviderForm } from './connect-provider-form'
import { ModelProviderCard } from './model-provider-card'

const KEY_RING_TEXT: Record<ModelProviderListDto['keyRing'], string | null> = {
  Same: null,
  Changed:
    'The key ring changed: the database holds values that this key ring cannot read. Restore the key folder from the same backup as the database, or enter each API key again.',
  NotWritable:
    'The key folder takes no write. Each saved API key still works, but a key that the server makes for the ring after this does not stay after a restart. Make the folder writable.',
}

const STATUS_LABELS = {
  NotRun: 'Not run',
  Running: 'Running',
  Clean: 'Clean',
  Stale: 'Stale',
  Failed: 'Failed',
} as const

function statusLabelOf({ cleanupStatus, cleanupFailure }: SessionSummaryDto) {
  return cleanupStatus === 'Failed' && cleanupFailure === 'Stopped' ? 'Stopped' : STATUS_LABELS[cleanupStatus]
}

function Providers() {
  const providers = useModelProviders(true)

  if (providers.isPending) {
    return (
      <p role="status" aria-label="Loading model providers">
        Loading model providers
      </p>
    )
  }
  if (providers.isError) {
    return (
      <QueryError title="The model providers did not load." error={providers.error} onRetry={() => void providers.refetch()} />
    )
  }

  const warning = KEY_RING_TEXT[providers.data.keyRing]
  return (
    <>
      {warning && (
        <div role="alert" className="rounded-lg border border-destructive/20 bg-destructive/10 p-3 text-sm text-destructive">
          {warning}
        </div>
      )}
      <section aria-labelledby="providers-heading" className="flex flex-col gap-4">
        <h2 id="providers-heading" className="text-lg font-semibold">
          Connected
        </h2>
        {providers.data.items.length === 0 && (
          <p className="text-sm text-muted-foreground">No model provider is connected. Cleanup needs one.</p>
        )}
        {providers.data.items.map((provider) => (
          <ModelProviderCard key={provider.id} provider={provider} />
        ))}
      </section>
      <Card>
        <CardHeader>
          <CardTitle className="text-lg">
            <h2>Connect a model provider</h2>
          </CardTitle>
        </CardHeader>
        <CardContent>
          <ConnectProviderForm />
        </CardContent>
      </Card>
    </>
  )
}

function RecentSessions() {
  const sessions = useRecentSessions()

  if (sessions.isPending) {
    return (
      <p role="status" aria-label="Loading Sessions">
        Loading Sessions
      </p>
    )
  }
  if (sessions.isError) {
    return <QueryError title="The Sessions did not load." error={sessions.error} onRetry={() => void sessions.refetch()} />
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-lg">
          <h2>Run a Cleanup</h2>
        </CardTitle>
      </CardHeader>
      <CardContent>
        {sessions.data.items.length === 0 && <p className="text-sm text-muted-foreground">You have no Session yet.</p>}
        <ul className="flex flex-col">
          {sessions.data.items.map((session) => (
            <li key={session.id} className="flex items-center gap-2 text-sm">
              <Link to="/model-providers" search={{ session: session.id }} className="flex min-h-11 min-w-0 flex-1 items-center sm:min-h-8">
                <span className="truncate">{session.preview || 'A Session with no words'}</span>
              </Link>
              <Badge variant="outline">{statusLabelOf(session)}</Badge>
            </li>
          ))}
        </ul>
      </CardContent>
    </Card>
  )
}

export function ModelProvidersPage({ sessionId }: { sessionId?: string }) {
  const membership = useMembership()
  const mightBeAnAdmin = membership.status === 'member' && membership.user.role === 'Admin'

  return (
    <div className="mx-auto flex w-full max-w-4xl flex-col gap-6 px-4 py-8 max-sm:[&_[data-slot=button]]:h-11 max-sm:[&_[data-slot=input]]:h-11 max-sm:[&_[data-slot=select-trigger]]:h-11">
      <PageHeading>Model providers</PageHeading>
      {sessionId ? <CleanupCard sessionId={sessionId} /> : <RecentSessions />}
      {mightBeAnAdmin ? (
        <Providers />
      ) : (
        membership.status !== 'loading' && (
          <p className="text-sm text-muted-foreground">An Admin connects the model provider of this install.</p>
        )
      )}
    </div>
  )
}
