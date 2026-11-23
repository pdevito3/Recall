import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, expect, test, vi } from 'vitest'
import { commands, page, userEvent } from 'vitest/browser'
import { render } from 'vitest-browser-react'

import { JournalEditor } from './journal-editor'

const longText = 'river stone morning coffee '.repeat(4000)

let client: QueryClient

const noPeople = {
  method: 'GET' as const,
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

test('the @ picker moves back into the window when the window narrows while it is open', async () => {
  await page.viewport(1280, 800)
  const jane = { id: 'per_1', label: 'Jane', fullName: 'Jane Park', notes: null, version: 1 }
  await commands.answerRequests([{ ...noPeople, body: [jane], headers: { 'x-pagination': noPeople.headers['x-pagination'].replace('"totalCount":0', '"totalCount":1') } }])
  client = new QueryClient()
  const screen = await render(
    <QueryClientProvider client={client}>
      <JournalEditor initialText="tea with jane and a walk by the river after lunch" revision={1} label="New entry" onChange={() => undefined} />
    </QueryClientProvider>,
  )
  const editor = screen.getByRole('textbox', { name: 'New entry' })
  await userEvent.click(editor)
  await userEvent.keyboard('{End} @Ja')
  const picker = screen.getByRole('listbox', { name: 'People' })
  await expect.element(picker.getByRole('option', { name: 'Jane Jane Park' })).toBeVisible()
  expect(picker.element().getBoundingClientRect().left).toBeGreaterThan(320)

  await page.viewport(320, 800)

  await vi.waitFor(() => expect(picker.element().getBoundingClientRect().right).toBeLessThanOrEqual(320))
  expect(picker.element().getBoundingClientRect().left).toBeGreaterThanOrEqual(0)
})
