import { useQueryClient } from '@tanstack/react-query'
import { createFileRoute, useNavigate } from '@tanstack/react-router'
import { useRef } from 'react'

import { Notification } from '@/components/notification'
import { useBreadcrumbName } from '@/components/breadcrumb-name'
import { Page } from '@/components/page'
import { PageHeading } from '@/components/page-heading'
import { QueryError } from '@/components/query-error'
import { useDeleteUser } from '@/domain/users/apis/delete-user'
import { useUser } from '@/domain/users/apis/get-user'
import { useUpdateUserRole } from '@/domain/users/apis/update-user-role'
import { UserKeys } from '@/domain/users/apis/user.keys'
import { UserActions } from '@/domain/users/components/user-actions'
import { ROLE_LABELS } from '@/domain/users/components/user-role'
import { leaveDeletedRecord } from '@/lib/leave-deleted-record'

export const Route = createFileRoute('/users/$id/')({
  component: UserPage,
  staticData: { breadcrumb: 'User', breadcrumbParent: { title: 'Users', to: '/users' } },
})

function UserPage() {
  const { id } = Route.useParams()
  const user = useUser(id)
  const changeRole = useUpdateUserRole(id)
  const remove = useDeleteUser()
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  const headingRef = useRef<HTMLHeadingElement>(null)
  useBreadcrumbName(user.data?.fullName)

  if (user.isPending) {
    return (
      <Page>
        <p role="status" aria-label="Loading user" className="text-muted-foreground">Loading user</p>
      </Page>
    )
  }

  if (user.isError) {
    return (
      <Page>
        <QueryError title="The user did not load." error={user.error} onRetry={() => user.refetch()} />
      </Page>
    )
  }

  const { fullName, email, username, role } = user.data

  const handleDelete = () =>
    remove.mutate(id, {
      onSuccess: () => {
        Notification.success('User deleted')
        return leaveDeletedRecord(queryClient, {
          detail: UserKeys.detail(id),
          lists: UserKeys.lists(),
          leave: () => navigate({ to: '/users' }),
        })
      },
      onError: () => headingRef.current?.focus(),
    })

  const pendingAction = (changeRole.isPending && 'UpdateRole') || (remove.isPending && 'Delete') || undefined

  return (
    <Page>
      <PageHeading ref={headingRef}>{fullName}</PageHeading>
      <section aria-label="Details">
        <dl className="grid max-w-md grid-cols-[auto_1fr] gap-x-6 gap-y-2 text-sm wrap-anywhere">
          <dt className="text-muted-foreground">Email</dt>
          <dd>{email ?? 'Not set'}</dd>
          <dt className="text-muted-foreground">Username</dt>
          <dd>{username}</dd>
          <dt className="text-muted-foreground">Role</dt>
          <dd>{ROLE_LABELS[role]}</dd>
        </dl>
      </section>
      <UserActions
        user={user.data}
        pendingAction={pendingAction}
        onChangeRole={(newRole) => changeRole.mutate(newRole, { onSettled: () => headingRef.current?.focus() })}
        onDelete={handleDelete}
      />
    </Page>
  )
}
