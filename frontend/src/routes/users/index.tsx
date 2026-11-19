import { createFileRoute } from '@tanstack/react-router'
import * as z from 'zod'

import { getUserListQueryPageNumberSchema } from '@/api/generated/users/zod/get-user-list'
import { Page } from '@/components/page'
import { PageHeading } from '@/components/page-heading'
import { QueryError } from '@/components/query-error'
import { Link } from '@/components/ui/link'
import { Pager } from '@/components/ui/pager'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { useMembership } from '@/domain/membership/apis/get-membership'
import { useUserList } from '@/domain/users/apis/get-user-list'
import { ROLE_LABELS } from '@/domain/users/components/user-role'
import { useIsMobile } from '@/hooks/use-mobile'

const listSearchSchema = z.object({
  page: getUserListQueryPageNumberSchema.catch(undefined),
})

export const Route = createFileRoute('/users/')({
  validateSearch: listSearchSchema,
  component: UsersPage,
  staticData: { breadcrumb: 'Users' },
})

function UsersPage() {
  const { page = 1 } = Route.useSearch()
  const navigate = Route.useNavigate()
  const membership = useMembership().status
  const list = useUserList({ page }, { enabled: membership !== 'loading' })

  const handlePageChange = (pageNumber: number) =>
    navigate({ search: (prev) => ({ ...prev, page: pageNumber === 1 ? undefined : pageNumber }) })

  return (
    <Page>
      <PageHeading>Users</PageHeading>
      <UserRows list={list} page={page} onPageChange={handlePageChange} />
    </Page>
  )
}

interface UserRowsProps {
  list: ReturnType<typeof useUserList>
  page: number
  onPageChange: (pageNumber: number) => void
}

function UserRows({ list, page, onPageChange }: UserRowsProps) {
  const isMobile = useIsMobile()

  if (list.isPending) {
    return <p role="status" aria-label="Loading users" className="text-muted-foreground">Loading users</p>
  }

  if (list.isError) {
    return <QueryError title="The users did not load." error={list.error} onRetry={() => list.refetch()} />
  }

  const { users, pagination } = list.data

  if (users.length === 0 && page > 1) {
    return (
      <p className="text-muted-foreground">
        There are no users on page {page}.{' '}
        <Link to="/users" className="underline underline-offset-4 hover:text-primary">
          Go to page 1
        </Link>
      </p>
    )
  }

  if (users.length === 0) {
    return <p className="text-muted-foreground">There are no users.</p>
  }

  return (
    <div className="flex flex-col gap-4">
      <div className="rounded-md border">
        <Table aria-label="Users">
          <TableHeader>
            <TableHead isRowHeader>Name</TableHead>
            {!isMobile && <TableHead>Email</TableHead>}
            <TableHead>Role</TableHead>
          </TableHeader>
          <TableBody>
            {users.map((user) => (
              <TableRow key={user.id} id={user.id}>
                <TableCell className="wrap-anywhere">
                  <Link to="/users/$id" params={{ id: user.id }} className="font-medium max-md:inline-flex max-md:min-h-11 max-md:min-w-11 max-md:items-center underline-offset-4 hover:underline">
                    {user.fullName}
                  </Link>
                  {isMobile && user.email && <p className="text-sm text-muted-foreground">{user.email}</p>}
                </TableCell>
                {!isMobile && <TableCell className="wrap-anywhere">{user.email}</TableCell>}
                <TableCell className="whitespace-nowrap">{ROLE_LABELS[user.role]}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </div>
      <Pager label="Pages of users" pagination={pagination} onPageChange={onPageChange} />
    </div>
  )
}
