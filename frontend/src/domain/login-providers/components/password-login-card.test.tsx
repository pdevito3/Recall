import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { expect, test } from 'vitest'
import { commands, page } from 'vitest/browser'
import { render } from 'vitest-browser-react'

import type { LoginSettingsDto } from '@/api/generated/models/types/login-settings-dto'
import '@/lib/api-client'

import { PasswordLoginCard } from './password-login-card'

const path = '/api/v1/login-settings'
const on: LoginSettingsDto = { passwordLogin: 'On', version: 1 }
const off: LoginSettingsDto = { passwordLogin: 'Off', version: 2 }
const noLinkedAdmin = 'Each tenant needs an Admin who can sign in with a provider before password login turns off.'
const staleDetail = 'This record changed after you loaded it. Reload it, then make your change again.'
const staleWords = 'Password login changed after you loaded this page. The card shows the setting now.'

const conflict = (detail: string, type = 'https://tools.ietf.org/html/rfc9110#section-15.5.10') => ({
  method: 'PUT' as const,
  path,
  status: 409,
  body: { type, title: 'Conflict', status: 409, detail },
})

function renderCard() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <PasswordLoginCard />
    </QueryClientProvider>,
  )
}

const card = () => page.getByRole('region', { name: 'Password login' })
const confirmDialog = () => page.getByRole('dialog', { name: 'Turn password login off?' })

test('@IDP-13 shows On and offers to turn it off', async () => {
  await commands.answerRequests([{ method: 'GET', path, body: on }])
  await renderCard()

  await expect.element(card().getByText('On', { exact: true })).toBeVisible()
  await expect.element(card().getByText(/People can sign in with a password/)).toBeVisible()
  await expect.element(card().getByRole('button', { name: 'Turn password login off' })).toBeVisible()
})

test('@IDP-15 @IDP-20 shows Off, says what still works, and turns it on with no confirm', async () => {
  await commands.answerRequests([
    { method: 'GET', path, body: off },
    { method: 'PUT', path, body: { passwordLogin: 'On', version: 3 } },
  ])
  await renderCard()

  await expect.element(card().getByText('Off', { exact: true })).toBeVisible()
  await expect.element(card().getByText(/A temporary password only links a provider\. The claim-owner file/)).toBeVisible()
  await card().getByRole('button', { name: 'Turn password login on' }).click()

  await expect.element(card().getByText('On', { exact: true })).toBeVisible()
  expect(page.getByRole('dialog').query()).toBeNull()
  expect(await commands.takeSentRequests()).toEqual([{ method: 'PUT', path, body: { passwordLogin: 'On', version: 2 } }])
})

test('@IDP-13 the turn off asks first, says what changes, and sends the version that it read', async () => {
  await commands.answerRequests([
    { method: 'GET', path, body: on },
    { method: 'PUT', path, body: off },
  ])
  await renderCard()

  await card().getByRole('button', { name: 'Turn password login off' }).click()
  await expect.element(confirmDialog().getByText(/No one signs in with a password/)).toBeVisible()
  await expect.element(confirmDialog().getByText(/only links a provider/)).toBeVisible()
  await expect.element(confirmDialog().getByText(/claim-owner file of the server still works/)).toBeVisible()
  await confirmDialog().getByRole('button', { name: 'Keep password login on' }).click()
  await expect.element(page.getByRole('dialog')).not.toBeInTheDocument()
  expect(await commands.takeSentRequests()).toEqual([])

  await card().getByRole('button', { name: 'Turn password login off' }).click()
  await confirmDialog().getByRole('button', { name: 'Turn password login off' }).click()

  await expect.element(card().getByText('Off', { exact: true })).toBeVisible()
  await expect.element(page.getByRole('heading', { name: 'Password login' })).toHaveFocus()
  expect(await commands.takeSentRequests()).toEqual([{ method: 'PUT', path, body: { passwordLogin: 'Off', version: 1 } }])
})

test('@IDP-13 a refused turn off shows the message of the Server and stays On', async () => {
  await commands.answerRequests([{ method: 'GET', path, body: on }, conflict(noLinkedAdmin)])
  await renderCard()

  await card().getByRole('button', { name: 'Turn password login off' }).click()
  await confirmDialog().getByRole('button', { name: 'Turn password login off' }).click()

  await expect.element(card().getByRole('alert')).toHaveTextContent(noLinkedAdmin)
  await expect.element(card().getByText('On', { exact: true })).toBeVisible()
})

test('@IDP-13 a stale turn shows the message and reads the setting again', async () => {
  await commands.answerRequests([{ method: 'GET', path, body: on }, conflict(staleDetail, 'urn:recall:problem:stale-version')])
  await renderCard()
  await expect.element(card().getByText('On', { exact: true })).toBeVisible()

  // Another Admin turned it off after this page read it.
  await commands.answerRequests([{ method: 'GET', path, body: off }, conflict(staleDetail, 'urn:recall:problem:stale-version')])
  await card().getByRole('button', { name: 'Turn password login off' }).click()
  await confirmDialog().getByRole('button', { name: 'Turn password login off' }).click()

  await expect.element(card().getByRole('alert')).toHaveTextContent(staleWords)
  await expect.element(card().getByText('Off', { exact: true })).toBeVisible()
  await expect.element(card().getByRole('button', { name: 'Turn password login on' })).toBeVisible()
})

test('@IDP-13 a stale turn whose read again fails says that the setting did not load, and reads again on Try again', async () => {
  const failed = { method: 'GET' as const, path, status: 500, body: { title: 'Server error', status: 500, detail: 'The Server failed.' } }
  await commands.answerRequests([{ method: 'GET', path, body: on }, conflict(staleDetail, 'urn:recall:problem:stale-version')])
  await renderCard()
  await expect.element(card().getByText('On', { exact: true })).toBeVisible()

  await commands.answerRequests([failed, conflict(staleDetail, 'urn:recall:problem:stale-version')])
  await card().getByRole('button', { name: 'Turn password login off' }).click()
  await confirmDialog().getByRole('button', { name: 'Turn password login off' }).click()

  await expect.element(card().getByText(/The password login setting did not load/)).toBeVisible()
  expect(card().getByText(staleWords).elements()).toHaveLength(0)

  await commands.answerRequests([{ method: 'GET', path, body: off }])
  await card().getByRole('button', { name: 'Try again' }).click()

  await expect.element(card().getByText('Off', { exact: true })).toBeVisible()
  await expect.element(card().getByText(/The password login setting did not load/)).not.toBeInTheDocument()
  await expect.element(card().getByRole('alert')).toHaveTextContent(staleWords)
})

test('a failed load says so and offers a retry', async () => {
  await commands.answerRequests([{ method: 'GET', path, status: 500, body: { title: 'Server error', status: 500, detail: 'The Server failed.' } }])
  await renderCard()

  await expect.element(page.getByText('The password login setting did not load.')).toBeVisible()
  await expect.element(page.getByRole('button', { name: 'Try again' })).toBeVisible()
})
