import { createFileRoute } from '@tanstack/react-router'

import { Page } from '@/components/page'
import { QueryError } from '@/components/query-error'
import { Link } from '@/components/ui/link'
import { useSession } from '@/domain/sessions/apis/get-session'
import { SessionPage } from '@/domain/sessions/components/session-page'
import { isProblemDetailsError } from '@/lib/problem-details'

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
        isProblemDetailsError(session.error) && session.error.status === 404 ? (
          <p className="text-muted-foreground">
            This entry does not exist. It may have been deleted.{' '}
            <Link to="/" className="underline underline-offset-4 hover:text-primary">Go to your journal</Link>
          </p>
        ) : (
          <QueryError title="This entry did not load." error={session.error} onRetry={() => session.refetch()} />
        )
      )}
      {session.data && <SessionPage key={session.data.id} session={session.data} />}
    </Page>
  )
}
