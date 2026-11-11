import { expect, test, vi } from 'vitest'
import { render } from 'vitest-browser-react'

import { WordsLeftAtSignOut } from './words-left-at-sign-out'

const doc = (text: string) => JSON.stringify({ type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text }] }] })

test('each text that only the page holds has its own Copy, and the page goes only at Leave', async () => {
  const copied: string[] = []
  vi.spyOn(navigator.clipboard, 'writeText').mockImplementation((text) => {
    copied.push(text)
    return Promise.resolve()
  })
  const leave = vi.fn()
  const screen = await render(<WordsLeftAtSignOut why="signedOutInAnotherTab" words={[doc('first words'), doc('second words')]} leave={leave} />)

  await expect.element(screen.getByText('Some words of these entries did not reach the server', { exact: false })).toBeVisible()
  const list = screen.getByRole('list', { name: 'Entries with words that only this page holds' })
  await expect.element(list.getByText('first words')).toBeVisible()
  await list.getByRole('button', { name: 'Copy words' }).nth(1).click()
  await vi.waitFor(() => expect(copied).toEqual(['second words']))
  expect(leave).not.toHaveBeenCalled()

  await screen.getByRole('button', { name: 'Leave this page' }).click()
  expect(leave).toHaveBeenCalledOnce()
})

test.each([
  ['signedOut', 'You signed out'],
  ['signedOutInAnotherTab', 'You signed out in another tab'],
  ['sessionEnded', 'Your session ended'],
  ['sessionUnchecked', 'We could not check your session'],
  ['pageDidNotLoad', 'This page did not load'],
] as const)('the screen after %s names what happened', async (why, title) => {
  const screen = await render(<WordsLeftAtSignOut why={why} words={[doc('first words')]} leave={vi.fn()} />)

  await expect.element(screen.getByRole('heading', { level: 1, name: title })).toBeVisible()
  await expect.element(screen.getByText('Some words of this entry did not reach the server', { exact: false })).toBeVisible()
})
