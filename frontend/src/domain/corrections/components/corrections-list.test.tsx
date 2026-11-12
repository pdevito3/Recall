import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { createMemoryHistory, createRouter, RouterProvider } from '@tanstack/react-router'
import { expect, test } from 'vitest'
import { commands, userEvent } from 'vitest/browser'
import { render } from 'vitest-browser-react'

import type { CorrectionDto } from '@/api/generated/models/types/correction-dto'
import { routeTree } from '@/routeTree.gen'
import type { Answer } from '@/test/answer'
import { signedInMember } from '@/test/session'

const pagination = {
  'x-pagination': JSON.stringify({
    totalCount: 2,
    pageSize: 100,
    currentPageSize: 2,
    currentStartIndex: 1,
    currentEndIndex: 2,
    pageNumber: 1,
    totalPages: 1,
    hasPrevious: false,
    hasNext: false,
  }),
}

const grdb: CorrectionDto = { id: 'cor_1', canonicalTerm: 'GRDB', mishearings: ['grid b'], hardReplace: false, version: 1 }
const cafe: CorrectionDto = { id: 'cor_2', canonicalTerm: 'Café Lumière', mishearings: [], hardReplace: false, version: 1 }

const listAnswer: Answer = { method: 'GET', path: '/api/v1/corrections', headers: pagination, body: [cafe, grdb] }

const staleVersion = {
  type: 'urn:recall:problem:stale-version',
  title: 'The record changed after you loaded it.',
  status: 409,
  detail: 'This record changed after you loaded it. Reload it, then make your change again.',
  errors: { version: ['This record changed after you loaded it. Reload it, then make your change again.'] },
}

async function renderPageAt(path: string, answers: Answer[] = []) {
  await commands.answerRequests([...signedInMember, listAnswer, ...answers])
  const router = createRouter({ routeTree, history: createMemoryHistory({ initialEntries: [path] }) })
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const screen = await render(
    <QueryClientProvider client={client}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  )
  return { screen, router }
}

test('reads the search from the URL and finds a mishearing', async () => {
  // Arrange and Act
  const { screen } = await renderPageAt('/corrections?search=GRID')

  // Assert
  const list = screen.getByRole('list', { name: 'Your corrections' })
  await expect.element(list.getByText('GRDB', { exact: true })).toBeVisible()
  await expect.element(list.getByText('Café Lumière')).not.toBeInTheDocument()
})

test('puts the search in the URL', async () => {
  // Arrange
  const { screen, router } = await renderPageAt('/corrections')
  await expect.element(screen.getByText('Café Lumière')).toBeVisible()

  // Act
  await userEvent.fill(screen.getByRole('searchbox', { name: 'Search corrections' }), 'lumiere')

  // Assert
  await expect.element(screen.getByText('GRDB', { exact: true })).not.toBeInTheDocument()
  expect(router.state.location.search).toEqual({ search: 'lumiere' })
})

test('a stale save keeps the edits, and the latest takes the field that another save changed', async () => {
  // Arrange
  const latest: CorrectionDto = { ...grdb, hardReplace: true, version: 2 }
  const { screen } = await renderPageAt('/corrections', [
    { method: 'PUT', path: '/api/v1/corrections/cor_1', status: 409, body: staleVersion },
    { method: 'GET', path: '/api/v1/corrections/cor_1', body: latest },
  ])
  await userEvent.click(screen.getByRole('button', { name: 'Edit GRDB' }))
  const dialog = screen.getByRole('dialog')
  await userEvent.fill(dialog.getByRole('textbox', { name: 'Mishearings' }), 'grid b\ngreat db')
  await userEvent.click(dialog.getByRole('button', { name: 'Save correction' }))
  await expect.element(dialog.getByText('This correction changed after you opened it.')).toBeVisible()

  // Act
  await userEvent.click(dialog.getByRole('button', { name: 'Load the latest' }))

  // Assert
  await expect.element(dialog.getByRole('status')).toHaveTextContent(
    'A save after you opened this correction changed Always replace. The form shows the saved values of these fields. Your edits to the other fields are still in the form. Check the form, then save.',
  )
  await expect.element(dialog.getByRole('textbox', { name: 'Mishearings' })).toHaveValue('grid b\ngreat db')
  await expect.element(dialog.getByRole('checkbox', { name: 'Always replace' })).toBeChecked()
  await commands.answerRequests([
    ...signedInMember,
    listAnswer,
    { method: 'PUT', path: '/api/v1/corrections/cor_1', body: { ...latest, mishearings: ['grid b', 'great db'], version: 3 } },
  ])
  await commands.takeSentRequests()
  await userEvent.click(dialog.getByRole('button', { name: 'Save correction' }))
  await expect.element(screen.getByRole('dialog')).not.toBeInTheDocument()
  expect(await commands.takeSentRequests()).toEqual([
    {
      method: 'PUT',
      path: '/api/v1/corrections/cor_1',
      body: { canonicalTerm: 'GRDB', mishearings: ['grid b', 'great db'], hardReplace: true, version: 2 },
    },
  ])
})

test('a second Enter while the save is out does not send it again', async () => {
  // Arrange
  const { screen } = await renderPageAt('/corrections', [
    { method: 'POST', path: '/api/v1/corrections', status: 201, body: { ...grdb, id: 'cor_3', canonicalTerm: 'Tallinn' } },
  ])
  await userEvent.click(screen.getByRole('button', { name: 'Add correction', exact: true }))
  const dialog = screen.getByRole('dialog')
  await userEvent.fill(dialog.getByRole('textbox', { name: 'Term' }), 'Tallinn')
  await commands.takeSentRequests()
  await commands.holdRequests('/api/v1/corrections')

  // Act
  await userEvent.keyboard('{Enter}')
  await expect.element(dialog.getByRole('button', { name: 'Save correction' })).toHaveAttribute('data-pending', 'true')
  await userEvent.keyboard('{Enter}')
  await commands.releaseRequests('/api/v1/corrections')

  // Assert
  await expect.element(screen.getByRole('dialog')).not.toBeInTheDocument()
  const sent = await commands.takeSentRequests()
  expect(sent.filter((request) => request.method === 'POST')).toHaveLength(1)
})

test('two submits in one task send one POST', async () => {
  // Arrange
  const { screen } = await renderPageAt('/corrections', [
    { method: 'POST', path: '/api/v1/corrections', status: 201, body: { ...grdb, id: 'cor_3', canonicalTerm: 'Tallinn' } },
  ])
  await userEvent.click(screen.getByRole('button', { name: 'Add correction', exact: true }))
  const dialog = screen.getByRole('dialog')
  await userEvent.fill(dialog.getByRole('textbox', { name: 'Term' }), 'Tallinn')
  await commands.takeSentRequests()
  await commands.holdRequests('/api/v1/corrections')

  // Act
  const form = dialog.getByRole('textbox', { name: 'Term' }).element().closest('form')!
  form.requestSubmit()
  form.requestSubmit()
  await expect.element(dialog.getByRole('button', { name: 'Save correction' })).toHaveAttribute('data-pending', 'true')
  await commands.releaseRequests('/api/v1/corrections')

  // Assert
  await expect.element(screen.getByRole('dialog')).not.toBeInTheDocument()
  const sent = await commands.takeSentRequests()
  expect(sent.filter((request) => request.method === 'POST')).toHaveLength(1)
})

test('a delete of a correction that another device deleted puts the focus on the heading', async () => {
  // Arrange
  const notFound = { type: 'urn:recall:problem:not-found', title: 'Not found', status: 404, detail: 'The correction was not found.' }
  const { screen } = await renderPageAt('/corrections')
  await userEvent.click(screen.getByRole('button', { name: 'Delete GRDB' }))
  await commands.answerRequests([
    ...signedInMember,
    { method: 'GET', path: '/api/v1/corrections', headers: pagination, body: [cafe] },
    { method: 'DELETE', path: '/api/v1/corrections/cor_1', status: 404, body: notFound },
  ])

  // Act
  await userEvent.click(screen.getByRole('button', { name: 'Delete correction' }))

  // Assert
  await expect.element(screen.getByRole('button', { name: 'Delete GRDB' })).not.toBeInTheDocument()
  await expect.element(screen.getByRole('heading', { level: 1, name: 'Corrections' })).toHaveFocus()
})

test('a delete that the Server fails keeps the focus on the row that is still there', async () => {
  // Arrange
  const failed = { type: 'about:blank', title: 'Internal Server Error', status: 500, detail: 'Something went wrong.' }
  const { screen } = await renderPageAt('/corrections', [{ method: 'DELETE', path: '/api/v1/corrections/cor_1', status: 500, body: failed }])
  await userEvent.click(screen.getByRole('button', { name: 'Delete GRDB' }))

  // Act
  await userEvent.click(screen.getByRole('button', { name: 'Delete correction' }))

  // Assert
  await expect.element(screen.getByRole('dialog')).not.toBeInTheDocument()
  await expect.element(screen.getByRole('button', { name: 'Delete GRDB' })).toHaveFocus()
})

test('the term takes as many emoji as the Server takes characters', async () => {
  // Arrange
  const term = '😀'.repeat(150)
  const { screen } = await renderPageAt('/corrections', [
    { method: 'POST', path: '/api/v1/corrections', status: 201, body: { ...grdb, id: 'cor_3', canonicalTerm: term } },
  ])
  await userEvent.click(screen.getByRole('button', { name: 'Add correction', exact: true }))
  const dialog = screen.getByRole('dialog')
  await userEvent.fill(dialog.getByRole('textbox', { name: 'Term' }), term)
  await commands.takeSentRequests()

  // Act
  await userEvent.click(dialog.getByRole('button', { name: 'Save correction' }))

  // Assert
  await expect.element(screen.getByRole('dialog')).not.toBeInTheDocument()
  const [sent] = await commands.takeSentRequests()
  expect(sent?.body).toMatchObject({ canonicalTerm: term })
})
