import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { expect, test } from 'vitest'
import { commands, page } from 'vitest/browser'
import { render } from 'vitest-browser-react'

import '@/lib/api-client'

import { changePasswordPath } from '../apis/change-password'
import { ChangePasswordForm } from './change-password-form'

const current = 'the current journal passphrase'
const next = 'a new journal passphrase for me'

function renderForm() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <ChangePasswordForm loginName="ada" />
    </QueryClientProvider>,
  )
}

async function fill(currentPassword: string, newPassword: string, repeatPassword = newPassword) {
  await page.getByLabelText('Current password').fill(currentPassword)
  await page.getByLabelText('New password', { exact: true }).fill(newPassword)
  await page.getByLabelText('New password again').fill(repeatPassword)
  await page.getByRole('button', { name: 'Change password' }).click()
}

test('@PWD-17 a short or a repeated password that differs is refused in the form, with no request', async () => {
  await commands.answerRequests([{ method: 'POST', path: changePasswordPath, status: 500, body: {} }])
  await renderForm()

  await fill(current, 'short', 'shorter')

  await expect.element(page.getByText('Use at least 15 characters. A phrase of four words is long enough and easy to remember.', { exact: true })).toBeVisible()
  await expect.element(page.getByText('The two new passwords differ. Type the same password twice.')).toBeVisible()
  await expect.element(page.getByLabelText('New password', { exact: true })).toHaveFocus()
})

test('@PWD-17 a wrong current password shows the message of the Server at its field and keeps the values', async () => {
  const wrong = 'Your current password is wrong.'
  await commands.answerRequests([
    {
      method: 'POST',
      path: changePasswordPath,
      status: 403,
      body: { status: 403, detail: wrong, errors: { currentPassword: [wrong] } },
    },
  ])
  await renderForm()

  await fill('a wrong current passphrase', next)

  await expect.element(page.getByText(wrong)).toBeVisible()
  await expect.element(page.getByLabelText('Current password')).toHaveAttribute('aria-invalid', 'true')
  await expect.element(page.getByLabelText('New password', { exact: true })).toHaveValue(next)
})

test('@PWD-17 a change empties the form', async () => {
  await commands.answerRequests([{ method: 'POST', path: changePasswordPath, status: 204 }])
  await renderForm()

  await fill(current, next)

  await expect.element(page.getByLabelText('Current password')).toHaveValue('')
  await expect.element(page.getByLabelText('New password', { exact: true })).toHaveValue('')
})
