import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, expect, test, vi } from 'vitest'
import { commands } from 'vitest/browser'
import { render } from 'vitest-browser-react'

import { JournalEditor } from './journal-editor'

const longText = 'river stone morning coffee '.repeat(4000)

let client: QueryClient

const noPeople = {
  method: 'GET',
  path: '/api/v1/people',
  body: [],
  headers: {
    'x-pagination': JSON.stringify({ totalCount: 0, pageSize: 100, currentPageSize: 0, currentStartIndex: 0, currentEndIndex: 0, pageNumber: 1, totalPages: 0, hasPrevious: false, hasNext: false }),
  },
}

// A read that ends after this test would count as a request of the next test, which has no answer for it yet.
afterEach(() => vi.waitFor(() => expect(client.isFetching()).toBe(0)))

const renderEditor = async (isBounded: boolean) => {
  await commands.answerRequests([noPeople])
  client = new QueryClient()
  return render(
    <QueryClientProvider client={client}>
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
