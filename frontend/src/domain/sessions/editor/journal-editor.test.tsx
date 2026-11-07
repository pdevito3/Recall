import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { expect, test } from 'vitest'
import { commands } from 'vitest/browser'
import { render } from 'vitest-browser-react'

import { JournalEditor } from './journal-editor'

const longText = 'river stone morning coffee '.repeat(4000)

const renderEditor = async (isBounded: boolean) => {
  await commands.answerRequests([{ method: 'GET', path: '/api/v1/people', body: [] }])
  return render(
    <QueryClientProvider client={new QueryClient()}>
      <JournalEditor initialText={longText} revision={1} label="New entry" isBounded={isBounded} onChange={() => undefined} />
    </QueryClientProvider>,
  )
}

test('a bounded editor stays in the view with a long text, and the words stay in it', async () => {
  const screen = await renderEditor(true)
  const editor = screen.getByRole('textbox', { name: 'New entry' })

  await expect.element(editor).toBeInTheDocument()

  const box = editor.element().parentElement
  expect(box?.clientHeight).toBeLessThanOrEqual(window.innerHeight * 0.6 + 1)
  expect(box?.scrollHeight).toBeGreaterThan(window.innerHeight)
})

test('an editor with no bound grows with the words', async () => {
  const screen = await renderEditor(false)
  const editor = screen.getByRole('textbox', { name: 'New entry' })

  await expect.element(editor).toBeInTheDocument()

  expect(editor.element().getBoundingClientRect().height).toBeGreaterThan(window.innerHeight)
})
