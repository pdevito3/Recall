import { createFileRoute } from '@tanstack/react-router'

import { Page } from '@/components/page'
import { QueryError } from '@/components/query-error'
import { useSession } from '@/domain/sessions/apis/get-session'
import { SessionPage } from '@/domain/sessions/components/session-page'

export const Route = createFileRoute('/sessions/$id/')({
  component: SessionRoute,
  staticData: { breadcrumb: 'Entry', breadcrumbParent: { title: 'Journal', to: '/' } },
})

function SessionRoute() {
  const { id } = Route.useParams()
  const session = useSession(id)

  return (
    <Page>
      {session.isPending && <p role="status" aria-label="Loading this entry" className="text-muted-foreground">Loading this entry</p>}
      {session.isError && !session.data && (
        <QueryError title="This entry did not load." error={session.error} onRetry={() => session.refetch()} />
      )}
      {session.data && <SessionPage key={session.data.id} session={session.data} />}
    </Page>
  )
}
