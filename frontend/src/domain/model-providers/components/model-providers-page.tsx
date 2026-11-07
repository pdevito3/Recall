import type { ModelProviderListDto } from '@/api/generated/models/types/model-provider-list-dto'
import { Page } from '@/components/page'
import { PageHeading } from '@/components/page-heading'
import { QueryError } from '@/components/query-error'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { useMembership } from '@/domain/membership/apis/get-membership'

import { useModelProviders } from '../apis/model-providers'
import { ConnectProviderForm } from './connect-provider-form'
import { ModelProviderCard } from './model-provider-card'

const KEY_RING_TEXT: Record<ModelProviderListDto['keyRing'], string | null> = {
  Same: null,
  Changed:
    'The key ring changed: the database holds values that this key ring cannot read. Restore the key folder from the same backup as the database, or enter each API key again.',
  NotWritable:
    'The key folder takes no write. Each saved API key still works, but a key that the server makes for the ring after this does not stay after a restart. Make the folder writable.',
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

export function ModelProvidersPage() {
  const membership = useMembership()
  const mightBeAnAdmin = membership.status === 'member' && membership.user.role === 'Admin'

  return (
    <Page>
      <PageHeading>Model providers</PageHeading>
      {mightBeAnAdmin ? (
        // A button and a field are 44 px high on a phone already, and a select is not.
        <div className="flex flex-col gap-6 max-md:[&_[data-slot=select-trigger]]:h-11">
          <Providers />
        </div>
      ) : (
        membership.status !== 'loading' && (
          <p className="text-sm text-muted-foreground">An Admin connects the model provider of this install.</p>
        )
      )}
    </Page>
  )
}
