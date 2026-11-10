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
  const screen = await render(<WordsLeftAtSignOut words={[doc('first words'), doc('second words')]} leave={leave} />)

  const list = screen.getByRole('list', { name: 'Words that only this page holds' })
  await expect.element(list.getByText('first words')).toBeVisible()
  await list.getByRole('button', { name: 'Copy words' }).nth(1).click()
  await vi.waitFor(() => expect(copied).toEqual(['second words']))
  expect(leave).not.toHaveBeenCalled()

  await screen.getByRole('button', { name: 'Leave this page' }).click()
  expect(leave).toHaveBeenCalledOnce()
})
