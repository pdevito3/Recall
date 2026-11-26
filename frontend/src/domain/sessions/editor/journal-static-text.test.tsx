import { expect, test } from 'vitest'
import { userEvent } from 'vitest/browser'
import { render } from 'vitest-browser-react'

import { JournalStaticText } from './journal-static-text'

const taskOf = (text: string, checked: boolean) => ({ type: 'taskItem', attrs: { checked }, content: [{ type: 'paragraph', content: [{ type: 'text', text }] }] })

const tasks = JSON.stringify({ type: 'doc', content: [{ type: 'taskList', content: [taskOf('Call the bank', false), taskOf('Buy bread', true)] }] })

test('a task item shows its check box off, with its state and the words of the item as its name, and a click does not change it', async () => {
  const screen = await render(<JournalStaticText text={tasks} />)

  const open = screen.getByRole('checkbox', { name: 'Call the bank' })
  const done = screen.getByRole('checkbox', { name: 'Buy bread' })
  await expect.element(open).toBeDisabled()
  await expect.element(open).not.toBeChecked()
  await expect.element(done).toBeDisabled()
  await expect.element(done).toBeChecked()

  await userEvent.click(open, { force: true })

  await expect.element(open).not.toBeChecked()
})
