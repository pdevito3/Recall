import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { expect, test } from 'vitest'
import { commands, page } from 'vitest/browser'
import { render } from 'vitest-browser-react'

import type { UserDto } from '@/api/generated/models/types/user-dto'
import '@/lib/api-client'

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
const issued = { loginName: 'bob', password: 'acorn-ladder-velvet-tundra', expiresAt: '2026-11-28T10:00:00Z' }

async function renderDialog(user: UserDto, isSelf = false) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <TemporaryPasswordDialog user={user} isSelf={isSelf} />
    </QueryClientProvider>,
  )
}

test('@PWD-14 the first issue asks for a login name, warns of the end of the sessions, and shows the password once', async () => {
  await commands.answerRequests([{ method: 'POST', path, body: issued }])
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
  await commands.answerRequests([{ method: 'POST', path, body: issued }])
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
  await commands.answerRequests([
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

test('@PWD-14 a caller who sets their own password is told that this session ends', async () => {
  await renderDialog({ ...bob, loginName: 'bob' }, true)

  await page.getByRole('button', { name: 'Set temporary password' }).click()

  await expect.element(page.getByText(/also this one, so Recall signs you out/)).toBeVisible()
})
