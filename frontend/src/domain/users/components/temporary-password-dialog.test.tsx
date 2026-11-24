import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { createMemoryHistory, createRootRoute, createRouter, RouterProvider } from '@tanstack/react-router'
import { expect, test } from 'vitest'
import { commands, page } from 'vitest/browser'
import { render } from 'vitest-browser-react'

import type { UserDto } from '@/api/generated/models/types/user-dto'
import '@/lib/api-client'

import { LoginProviderKeys } from '@/domain/login-providers/apis/login-providers.keys'

import { TemporaryPasswordDialog } from './temporary-password-dialog'

const bob: UserDto = {
  id: 'usr_0199f0c1a2b37c4d8e9f0a1b2c3d4e5f',
  tenantId: 'ten_0199f0c1a2b37c4d8e9f0a1b2c3d4e5f',
  firstName: 'Bob',
  lastName: 'Smith',
  fullName: 'Bob Smith',
  identifier: 'bob',
  email: 'bob@example.com',
  username: 'bob',
  role: 'User',
  permissions: [],
  allowedActions: ['IssueTemporaryPassword'],
  version: 1,
}

const path = `/api/v1/users/${bob.id}/temporary-password`
const passwordLogin = (state: 'On' | 'Off') => ({ method: 'GET' as const, path: '/api/v1/login-settings', body: { passwordLogin: state, version: 1 } })
const issued = { loginName: 'bob', password: 'acorn-ladder-velvet-tundra', expiresAt: '2026-11-28T10:00:00Z' }

async function renderDialog(user: UserDto, isSelf = false, client = new QueryClient({ defaultOptions: { queries: { retry: false } } })) {
  const rootRoute = createRootRoute({ component: () => <TemporaryPasswordDialog user={user} isSelf={isSelf} /> })
  const router = createRouter({ routeTree: rootRoute, history: createMemoryHistory({ initialEntries: ['/'] }) })
  return render(
    <QueryClientProvider client={client}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  )
}

test('@PWD-14 the first issue asks for a login name, warns of the end of the sessions, and shows the password once', async () => {
  await commands.answerRequests([passwordLogin('On'), { method: 'POST', path, body: issued }])
  await renderDialog(bob)

  await page.getByRole('button', { name: 'Set temporary password' }).click()
  const ask = page.getByRole('dialog', { name: 'Set a temporary password for Bob Smith?' })
  await expect.element(ask.getByText(/ends each session and personal access token of Bob Smith/)).toBeVisible()
  await ask.getByRole('button', { name: 'Set temporary password' }).click()
  await expect.element(ask.getByText('Give the person a login name. They type it to sign in.')).toBeVisible()

  await ask.getByLabelText('Login name').fill('bob')
  await ask.getByRole('button', { name: 'Set temporary password' }).click()

  const result = page.getByRole('dialog', { name: 'Give Bob Smith this password' })
  await expect.element(result.getByText(issued.password)).toBeVisible()
  await expect.element(result.getByText(/works for 7 days/)).toBeVisible()
  await expect.element(result.getByRole('button', { name: 'Copy password' })).toBeVisible()
  await result.getByRole('button', { name: 'Done' }).click()
  await expect.element(page.getByRole('dialog')).not.toBeInTheDocument()

  await page.getByRole('button', { name: 'Set temporary password' }).click()
  await expect.element(page.getByText(issued.password)).not.toBeInTheDocument()
})

test('@PWD-14 a later issue shows the login name and asks for none', async () => {
  await commands.answerRequests([passwordLogin('On'), { method: 'POST', path, body: issued }])
  await renderDialog({ ...bob, loginName: 'bob' })

  await page.getByRole('button', { name: 'Set temporary password' }).click()
  const ask = page.getByRole('dialog', { name: 'Set a temporary password for Bob Smith?' })
  await expect.element(ask.getByText('bob', { exact: true })).toBeVisible()
  expect(ask.getByRole('textbox').elements()).toHaveLength(0)
  await ask.getByRole('button', { name: 'Set temporary password' }).click()

  await expect.element(page.getByText(issued.password)).toBeVisible()
})

test('@PWD-14 a login name in use shows the refusal on the field and keeps the dialog open', async () => {
  const message = 'This login name is in use. Pick another.'
  await commands.answerRequests([passwordLogin('On'), 
    {
      method: 'POST',
      path,
      status: 409,
      contentType: 'application/problem+json',
      body: JSON.stringify({ type: 'https://tools.ietf.org/html/rfc9110#section-15.5.10', title: 'Conflict', status: 409, detail: message, errors: { loginName: [message] } }),
    },
  ])
  await renderDialog(bob)

  await page.getByRole('button', { name: 'Set temporary password' }).click()
  await page.getByLabelText('Login name').fill('taken')
  await page.getByRole('dialog').getByRole('button', { name: 'Set temporary password' }).click()

  await expect.element(page.getByRole('dialog').getByText(message)).toBeVisible()
  await expect.element(page.getByLabelText('Login name')).toHaveValue('taken')
})

test('@PWD-14 a caller who sets their own password is told that this session stays', async () => {
  await commands.answerRequests([passwordLogin('On'), { method: 'POST', path, body: issued }])
  await renderDialog({ ...bob, loginName: 'bob' }, true)

  await page.getByRole('button', { name: 'Set temporary password' }).click()
  const ask = page.getByRole('dialog', { name: 'Set a temporary password for Bob Smith?' })
  await expect.element(ask.getByText(/ends each of your other sessions and personal access tokens\. You stay signed in here\./)).toBeVisible()
  await ask.getByRole('button', { name: 'Set temporary password' }).click()

  await expect.element(page.getByRole('dialog', { name: 'Give yourself this password' }).getByText(issued.password)).toBeVisible()
})

test('@IDP-20 while password login is off, the ask and the password say that it only links a provider', async () => {
  await commands.answerRequests([passwordLogin('Off'), { method: 'POST', path, body: issued }])
  await renderDialog({ ...bob, loginName: 'bob' })

  await page.getByRole('button', { name: 'Set temporary password' }).click()
  const ask = page.getByRole('dialog', { name: 'Set a temporary password for Bob Smith?' })
  await expect.element(ask.getByText(/Password login is off, so Recall makes a password that works for 7 days, and only to link a provider/)).toBeVisible()
  await expect.element(ask.getByText(/so they sign in again\.$/)).toBeVisible()
  await ask.getByRole('button', { name: 'Set temporary password' }).click()

  const result = page.getByRole('dialog', { name: 'Give Bob Smith this password' })
  await expect.element(result.getByText(/only to link a provider at the first sign-in, because password login is off/)).toBeVisible()
  expect(result.getByText(/pick a new password/).elements()).toHaveLength(0)
})

test('@IDP-20 the ask says nothing of the password and holds back the issue until the setting loads', async () => {
  await commands.holdRequests('/api/v1/login-settings')
  await commands.answerRequests([passwordLogin('Off'), { method: 'POST', path, body: issued }])
  await renderDialog({ ...bob, loginName: 'bob' })

  await page.getByRole('button', { name: 'Set temporary password' }).click()
  const ask = page.getByRole('dialog', { name: 'Set a temporary password for Bob Smith?' })
  await expect.element(ask.getByRole('status')).toHaveTextContent('Loading the password login setting')
  await expect.element(ask.getByText(/pick a new password/)).not.toBeInTheDocument()
  await expect.element(ask.getByRole('button', { name: 'Set temporary password' })).toBeDisabled()

  await commands.releaseRequests('/api/v1/login-settings')

  await expect.element(ask.getByText(/only to link a provider/)).toBeVisible()
  await expect.element(ask.getByRole('button', { name: 'Set temporary password' })).toBeEnabled()
})

test('@IDP-20 a failed read of the setting says so, keeps the issue off, and reads again on Try again', async () => {
  await commands.answerRequests([
    { method: 'GET', path: '/api/v1/login-settings', status: 500, body: { title: 'Server error', status: 500, detail: 'The Server failed.' } },
  ])
  await renderDialog({ ...bob, loginName: 'bob' })

  await page.getByRole('button', { name: 'Set temporary password' }).click()
  const ask = page.getByRole('dialog', { name: 'Set a temporary password for Bob Smith?' })
  await expect.element(ask.getByRole('alert').getByText('The password login setting did not load, so Recall cannot say what this password does.')).toBeVisible()
  await expect.element(ask.getByRole('button', { name: 'Set temporary password' })).toBeDisabled()

  await commands.answerRequests([passwordLogin('On'), { method: 'POST', path, body: issued }])
  await ask.getByRole('button', { name: 'Try again' }).click()

  await expect.element(ask.getByText(/only to pick a new password/)).toBeVisible()
})

test('@IDP-20 the dialog reads the setting again when it opens, so a turn after the page loaded shows', async () => {
  await commands.answerRequests([passwordLogin('On'), { method: 'POST', path, body: issued }])
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  await renderDialog({ ...bob, loginName: 'bob' }, false, client)
  await expect.poll(() => client.getQueryData(LoginProviderKeys.settings())).toEqual({ passwordLogin: 'On', version: 1 })

  // Another Admin turns password login off after the page loaded.
  await commands.answerRequests([passwordLogin('Off'), { method: 'POST', path, body: issued }])
  await page.getByRole('button', { name: 'Set temporary password' }).click()

  const ask = page.getByRole('dialog', { name: 'Set a temporary password for Bob Smith?' })
  await expect.element(ask.getByText(/only to link a provider/)).toBeVisible()
  expect(ask.getByText(/pick a new password/).elements()).toHaveLength(0)
})
