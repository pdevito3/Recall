import { expect, onTestFinished, test, vi } from 'vitest'
import { page } from 'vitest/browser'
import { render } from 'vitest-browser-react'

import { ConflictNotice, MergedNotice, mergedNote } from './conflict-notice'

test('a save that lost says where the words are, and offers the newest words', async () => {
  const onShowNewest = vi.fn()
  const onKeep = vi.fn()
  const screen = await render(<ConflictNotice conflict="wordsMovedToHistory" onShowNewest={onShowNewest} onKeep={onKeep} />)

  await expect.element(screen.getByRole('alert').getByText('A newer save, or a recording, changed this entry after your words saved. The words on this page are safe in History.')).toBeVisible()
  await screen.getByRole('button', { name: 'Show the newest words' }).click()
  await screen.getByRole('button', { name: 'Add my words to the newest words' }).click()

  expect(onShowNewest).toHaveBeenCalledOnce()
  expect(onKeep).toHaveBeenCalledOnce()
})

test('at 320 px with a font of 24 px each answer stays inside the notice', async () => {
  await page.viewport(320, 640)
  document.documentElement.style.fontSize = '24px'
  onTestFinished(() => {
    document.documentElement.style.fontSize = ''
  })
  const screen = await render(<ConflictNotice conflict="wordsMovedToHistory" onShowNewest={vi.fn()} onKeep={vi.fn()} />)

  const notice = screen.getByRole('alert').element().getBoundingClientRect()
  for (const name of ['Show the newest words', 'Add my words to the newest words']) {
    expect(screen.getByRole('button', { name }).element().getBoundingClientRect().right).toBeLessThanOrEqual(notice.right)
  }
  expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(320)
})

test('words that another tab took say that they are only on this page, and offer the same two answers', async () => {
  const screen = await render(<ConflictNotice conflict="discardedInAnotherTab" onShowNewest={vi.fn()} onKeep={vi.fn()} />)

  await expect.element(screen.getByRole('alert').getByText('Another tab took these words, and you may have discarded them there. They may now be only on this page.')).toBeVisible()
  await expect.element(screen.getByRole('button', { name: 'Show the newest words' })).toBeVisible()
  await expect.element(screen.getByRole('button', { name: 'Add my words to the newest words' })).toBeVisible()
})

test('a merge says that the words of the person are now in the newest words, and stays until the person dismisses it', async () => {
  const onDismiss = vi.fn()
  const screen = await render(<MergedNotice onDismiss={onDismiss} />)

  await expect.element(screen.getByRole('status').getByText(mergedNote)).toBeVisible()
  await screen.getByRole('button', { name: 'Dismiss' }).click()

  expect(onDismiss).toHaveBeenCalledOnce()
})

test('at 320 px with a font of 24 px the note of a merge keeps Dismiss inside it', async () => {
  await page.viewport(320, 640)
  document.documentElement.style.fontSize = '24px'
  onTestFinished(() => {
    document.documentElement.style.fontSize = ''
  })
  const screen = await render(<MergedNotice onDismiss={vi.fn()} />)

  const notice = screen.getByRole('status').element().getBoundingClientRect()
  expect(screen.getByRole('button', { name: 'Dismiss' }).element().getBoundingClientRect().right).toBeLessThanOrEqual(notice.right)
  expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(320)
})
