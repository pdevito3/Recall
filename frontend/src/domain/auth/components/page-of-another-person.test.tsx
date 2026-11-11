import { expect, test } from 'vitest'
import { render } from 'vitest-browser-react'

import { PageOfAnotherPerson } from './page-of-another-person'

test('a page that lost words says that they are gone, and does not say that they wait', async () => {
  const screen = await render(<PageOfAnotherPerson hasLostWords />)

  await expect.element(screen.getByText(/they are gone\./)).toBeVisible()
  await expect.element(screen.getByText(/wait on this device/)).not.toBeInTheDocument()
  await expect.element(screen.getByRole('link', { name: 'Sign in' })).toHaveAttribute('href', '/auth/login?anotherPerson=1&wordsLost=1')
})

test('a page that lost no words says that its unsent words wait', async () => {
  const screen = await render(<PageOfAnotherPerson hasLostWords={false} />)

  await expect.element(screen.getByText(/wait on this device\./)).toBeVisible()
  await expect.element(screen.getByRole('link', { name: 'Sign in' })).toHaveAttribute('href', '/auth/login?anotherPerson=1')
})
