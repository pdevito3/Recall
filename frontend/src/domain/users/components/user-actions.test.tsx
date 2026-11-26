import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { createMemoryHistory, createRootRoute, createRouter, RouterProvider } from '@tanstack/react-router'
import { expect, test, vi } from 'vitest'
import { commands, page } from 'vitest/browser'
import { render } from 'vitest-browser-react'

import type { UserDto } from '@/api/generated/models/types/user-dto'

import { UserActions } from './user-actions'

function aUser(allowedActions: UserDto['allowedActions']): UserDto {
  return {
    id: 'usr_0199f0c1a2b37c4d8e9f0a1b2c3d4e5f',
    tenantId: 'ten_0199f0c1a2b37c4d8e9f0a1b2c3d4e5f',
    firstName: 'Bob',
    lastName: 'Smith',
    fullName: 'Bob Smith',
    identifier: 'bob',
    email: 'bob@example.com',
    username: 'bob',
    roles: [],
    permissions: [],
    addedPermissions: [],
    removedPermissions: [],
    allowedActions,
    addableRoles: allowedActions.includes('AddRole') ? ['Admin', 'UserManager', 'Auditor'] : [],
    version: 1,
  }
}

const allActions: UserDto['allowedActions'] = ['Update', 'AddRole', 'AddPermission', 'RemovePermission', 'Delete']

test('shows a button for each action that the Server allows and that the page can send', async () => {
  await render(<UserActions user={aUser(allActions)} onAddRole={() => {}} onRemoveRole={() => {}} onDelete={() => {}} />)

  const actions = page.getByRole('group', { name: 'Actions for Bob Smith' })
  await expect.element(actions.getByRole('button', { name: 'Add role' })).toBeVisible()
  await expect.element(actions.getByRole('button', { name: 'Delete' })).toBeVisible()
  expect(actions.getByRole('button').elements()).toHaveLength(2)
})

test('@PWD-14 shows Set temporary password only when the Server allows it', async () => {
  await commands.answerRequests([{ method: 'GET', path: '/api/v1/login-settings', body: { passwordLogin: 'On', version: 1 } }])
  const rootRoute = createRootRoute({
    component: () => <UserActions user={aUser(['IssueTemporaryPassword'])} isSelf={false} onAddRole={() => {}} onRemoveRole={() => {}} onDelete={() => {}} />,
  })
  const router = createRouter({ routeTree: rootRoute, history: createMemoryHistory({ initialEntries: ['/'] }) })
  await render(
    <QueryClientProvider client={new QueryClient()}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  )

  await expect.element(page.getByRole('button', { name: 'Set temporary password' })).toBeVisible()
})

test('@PWD-14 hides Set temporary password until the page knows whether the user is the caller', async () => {
  await render(
    <QueryClientProvider client={new QueryClient()}>
      <UserActions user={aUser(['IssueTemporaryPassword', 'Delete'])} onAddRole={() => {}} onRemoveRole={() => {}} onDelete={() => {}} />
    </QueryClientProvider>,
  )

  await expect.element(page.getByRole('button', { name: 'Delete' })).toBeVisible()
  await expect.element(page.getByRole('button', { name: 'Set temporary password' })).not.toBeInTheDocument()
})

test('shows no actions when the Server allows none that the page can send', async () => {
  await render(<UserActions user={aUser(['Update', 'AddPermission'])} onAddRole={() => {}} onRemoveRole={() => {}} onDelete={() => {}} />)

  expect(page.getByRole('button').elements()).toHaveLength(0)
})

test('Add role offers only the addable roles of the Server, and sends the role that the user picks', async () => {
  const onAddRole = vi.fn()
  const user = { ...aUser(['AddRole']), roles: ['Auditor' as const], addableRoles: ['UserManager' as const] }
  await render(<UserActions user={user} onAddRole={onAddRole} onRemoveRole={() => {}} onDelete={() => {}} />)

  await page.getByRole('button', { name: 'Add role' }).click()
  const dialog = page.getByRole('dialog', { name: 'Add a role to Bob Smith' })
  await dialog.getByRole('button', { name: / Role$/ }).click()
  await expect.element(page.getByRole('option', { name: 'Auditor' })).not.toBeInTheDocument()
  await expect.element(page.getByRole('option', { name: 'Admin' })).not.toBeInTheDocument()
  await page.getByRole('option', { name: 'User manager' }).click()
  await dialog.getByRole('button', { name: 'Add role', exact: true }).click()

  await expect.element(dialog).not.toBeInTheDocument()
  await vi.waitFor(() => expect(onAddRole).toHaveBeenCalledExactlyOnceWith('UserManager'))
})

test('shows no Add role when the caller can add no role that the user lacks', async () => {
  const user = { ...aUser(['AddRole', 'Delete']), addableRoles: [] }
  await render(<UserActions user={user} onAddRole={() => {}} onRemoveRole={() => {}} onDelete={() => {}} />)

  await expect.element(page.getByRole('button', { name: 'Delete' })).toBeVisible()
  await expect.element(page.getByRole('button', { name: 'Add role' })).not.toBeInTheDocument()
})

test('Remove role offers only the roles that the user has, and Keep roles sends nothing', async () => {
  const onRemoveRole = vi.fn()
  const user = { ...aUser(['RemoveRole']), roles: ['Admin' as const, 'Auditor' as const] }
  await render(<UserActions user={user} onAddRole={() => {}} onRemoveRole={onRemoveRole} onDelete={() => {}} />)

  await page.getByRole('button', { name: 'Remove role' }).click()
  await page.getByRole('button', { name: 'Keep roles' }).click()
  await expect.element(page.getByRole('dialog')).not.toBeInTheDocument()
  expect(onRemoveRole).not.toHaveBeenCalled()

  await page.getByRole('button', { name: 'Remove role' }).click()
  const dialog = page.getByRole('dialog', { name: 'Remove a role from Bob Smith' })
  await dialog.getByRole('button', { name: / Role$/ }).click()
  await expect.element(page.getByRole('option', { name: 'User manager' })).not.toBeInTheDocument()
  await page.getByRole('option', { name: 'Auditor' }).click()
  await dialog.getByRole('button', { name: 'Remove role', exact: true }).click()

  await vi.waitFor(() => expect(onRemoveRole).toHaveBeenCalledExactlyOnceWith('Auditor'))
})

test('shows no Remove role for a Member, who has no role', async () => {
  await render(<UserActions user={aUser(['RemoveRole', 'Delete'])} onAddRole={() => {}} onRemoveRole={() => {}} onDelete={() => {}} />)

  await expect.element(page.getByRole('button', { name: 'Delete' })).toBeVisible()
  expect(page.getByRole('button', { name: 'Remove role' }).elements()).toHaveLength(0)
})

test('Delete asks before it deletes, and Keep user sends nothing', async () => {
  const onDelete = vi.fn()
  await render(<UserActions user={aUser(['Delete'])} onAddRole={() => {}} onRemoveRole={() => {}} onDelete={onDelete} />)

  await page.getByRole('button', { name: 'Delete' }).click()
  await page.getByRole('button', { name: 'Keep user' }).click()

  expect(onDelete).not.toHaveBeenCalled()

  await page.getByRole('button', { name: 'Delete' }).click()
  await page.getByRole('dialog', { name: 'Delete Bob Smith?' }).getByRole('button', { name: 'Delete user' }).click()

  expect(onDelete).toHaveBeenCalledOnce()
})

test('while one action runs, its button is pending and the other buttons take no press', async () => {
  await render(
    <UserActions user={aUser(['AddRole', 'Delete'])} pendingAction="Delete" onAddRole={() => {}} onRemoveRole={() => {}} onDelete={() => {}} />,
  )

  await expect.element(page.getByRole('button', { name: 'Add role' })).toBeDisabled()
})
