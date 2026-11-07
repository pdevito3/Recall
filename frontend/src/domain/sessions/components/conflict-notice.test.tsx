import { expect, test, vi } from 'vitest'
import { render } from 'vitest-browser-react'

import { ConflictNotice } from './conflict-notice'

test('a save that lost says where the words are, and offers the newest words', async () => {
  const onShowNewest = vi.fn()
  const onKeep = vi.fn()
  const screen = await render(<ConflictNotice conflict="wordsMovedToHistory" onShowNewest={onShowNewest} onKeep={onKeep} />)

  await expect.element(screen.getByRole('alert').getByText('A newer save, or a recording, changed this entry first. The words on this page are safe in History.')).toBeVisible()
  await screen.getByRole('button', { name: 'Show the newest words' }).click()
  await screen.getByRole('button', { name: 'Add my words to the newest words' }).click()

  expect(onShowNewest).toHaveBeenCalledOnce()
  expect(onKeep).toHaveBeenCalledOnce()
})

test('words that another tab took say that they are only on this page, and offer the same two answers', async () => {
  const screen = await render(<ConflictNotice conflict="discardedInAnotherTab" onShowNewest={vi.fn()} onKeep={vi.fn()} />)

  await expect.element(screen.getByRole('alert').getByText('Another tab took these words, and you may have discarded them there. They may now be only on this page.')).toBeVisible()
  await expect.element(screen.getByRole('button', { name: 'Show the newest words' })).toBeVisible()
  await expect.element(screen.getByRole('button', { name: 'Add my words to the newest words' })).toBeVisible()
})
