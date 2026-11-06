import { expect, test, vi } from 'vitest'
import { render } from 'vitest-browser-react'

import { ConflictNotice } from './conflict-notice'

test('a save that lost says where the words are, and offers the newest words', async () => {
  const onShowNewest = vi.fn()
  const onDismiss = vi.fn()
  const screen = await render(<ConflictNotice conflict="wordsMovedToHistory" onShowNewest={onShowNewest} onDismiss={onDismiss} />)

  await expect.element(screen.getByRole('alert').getByText('A newer save from another tab or device holds this entry now. The words on this page are safe in History.')).toBeVisible()
  await screen.getByRole('button', { name: 'Show the newest words' }).click()
  await screen.getByRole('button', { name: 'Keep these words' }).click()

  expect(onShowNewest).toHaveBeenCalledOnce()
  expect(onDismiss).toHaveBeenCalledOnce()
})

test('a save that replaced another says where the other save is, and has nothing newer to show', async () => {
  const screen = await render(<ConflictNotice conflict="replacedUnseenSave" onShowNewest={vi.fn()} onDismiss={vi.fn()} />)

  await expect.element(screen.getByRole('alert').getByText('These words replaced a save that this page had not shown. The other save is safe in History.')).toBeVisible()
  await expect.element(screen.getByRole('button', { name: 'Show the newest words' })).not.toBeInTheDocument()
})
