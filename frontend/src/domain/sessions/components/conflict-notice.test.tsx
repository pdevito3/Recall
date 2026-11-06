import { expect, test, vi } from 'vitest'
import { render } from 'vitest-browser-react'

import { ConflictNotice } from './conflict-notice'

test('a save that lost says where the words are, and offers the newest words', async () => {
  const onShowNewest = vi.fn()
  const onDismiss = vi.fn()
  const screen = await render(<ConflictNotice conflict="lost" onShowNewest={onShowNewest} onDismiss={onDismiss} />)

  await expect.element(screen.getByRole('alert')).toHaveTextContent(/The words on this page are safe in History\./)
  await screen.getByRole('button', { name: 'Show the newest words' }).click()
  await screen.getByRole('button', { name: 'Keep these words' }).click()

  expect(onShowNewest).toHaveBeenCalledOnce()
  expect(onDismiss).toHaveBeenCalledOnce()
})

test('a save that replaced another says where the other save is, and has nothing newer to show', async () => {
  const screen = await render(<ConflictNotice conflict="replaced" onShowNewest={vi.fn()} onDismiss={vi.fn()} />)

  await expect.element(screen.getByRole('alert')).toHaveTextContent(/The other save is safe in History\./)
  await expect.element(screen.getByRole('button', { name: 'Show the newest words' })).not.toBeInTheDocument()
})
