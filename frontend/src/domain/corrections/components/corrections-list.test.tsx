import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { createMemoryHistory, createRouter, RouterProvider } from '@tanstack/react-router'
import { afterEach, expect, test } from 'vitest'
import { commands, userEvent } from 'vitest/browser'
import { render } from 'vitest-browser-react'

import type { CorrectionDto } from '@/api/generated/models/types/correction-dto'
import { toastQueue } from '@/components/notification'
import { Toaster } from '@/components/toaster'
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
const cafe: CorrectionDto = { id: 'cor_2', canonicalTerm: 'Café Lumière', mishearings: ['caffe lumen'], hardReplace: false, version: 1 }
const radancy: CorrectionDto = { id: 'cor_4', canonicalTerm: 'Radancy', mishearings: [], hardReplace: true, version: 1 }

const listAnswer: Answer = { method: 'GET', path: '/api/v1/corrections', headers: pagination, body: [cafe, grdb, radancy] }

const staleVersion = {
  type: 'urn:recall:problem:stale-version',
  title: 'The record changed after you loaded it.',
  status: 409,
  detail: 'This record changed after you loaded it. Reload it, then make your change again.',
  errors: { version: ['This record changed after you loaded it. Reload it, then make your change again.'] },
}

afterEach(() => toastQueue.clear())

async function renderPageAt(path: string, answers: Answer[] = []) {
  await commands.answerRequests([...signedInMember, listAnswer, ...answers])
  const router = createRouter({ routeTree, history: createMemoryHistory({ initialEntries: [path] }) })
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const screen = await render(
    <QueryClientProvider client={client}>
      <RouterProvider router={router} />
      <Toaster />
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
  await userEvent.fill(screen.getByRole('searchbox', { name: 'Search misheard words' }), 'lumiere')

  // Assert
  await expect.element(screen.getByText('GRDB', { exact: true })).not.toBeInTheDocument()
  expect(router.state.location.search).toEqual({ search: 'lumiere' })
})

test('shows a known word as a pill and not in the list of corrections', async () => {
  // Arrange and Act
  const { screen } = await renderPageAt('/corrections')

  // Assert
  await expect.element(screen.getByRole('list', { name: 'Your known words' }).getByRole('listitem')).toHaveTextContent('Radancy')
  await expect.element(screen.getByRole('list', { name: 'Your corrections' }).getByRole('listitem')).toHaveLength(2)
  await expect.element(screen.getByRole('list', { name: 'Your corrections' }).getByText('Radancy')).not.toBeInTheDocument()
})

test('says that the list is empty when each correction is a known word', async () => {
  // Arrange and Act
  const { screen } = await renderPageAt('/corrections', [{ ...listAnswer, body: [radancy] }])

  // Assert
  await expect.element(screen.getByText('You have no misheard words yet. Add a word that dictation gets wrong.')).toBeVisible()
  await expect.element(screen.getByRole('button', { name: 'Remove Radancy' })).toBeVisible()
})

test('Enter adds the typed word as a known word, and the input stays ready for the next', async () => {
  // Arrange
  const tallinn: CorrectionDto = { id: 'cor_5', canonicalTerm: 'Tallinn', mishearings: [], hardReplace: false, version: 1 }
  const { screen } = await renderPageAt('/corrections')
  const input = screen.getByRole('textbox', { name: 'Known words' })
  await expect.element(input).toBeEnabled()
  await userEvent.fill(input, '  Tallinn ')
  await commands.answerRequests([
    ...signedInMember,
    { ...listAnswer, body: [cafe, grdb, radancy, tallinn] },
    { method: 'POST', path: '/api/v1/corrections', status: 201, body: tallinn },
  ])
  await commands.takeSentRequests()

  // Act
  await userEvent.keyboard('{Enter}')

  // Assert
  await expect.element(screen.getByRole('list', { name: 'Your known words' }).getByText('Tallinn')).toBeVisible()
  await expect.element(input).toHaveValue('')
  await expect.element(input).toHaveFocus()
  expect(await commands.takeSentRequests()).toEqual([
    { method: 'POST', path: '/api/v1/corrections', body: { canonicalTerm: 'Tallinn', mishearings: [], hardReplace: false } },
  ])
})

test('a word that is already a known word or a correction, in any case, is refused with no request', async () => {
  // Arrange
  const { screen } = await renderPageAt('/corrections')
  const input = screen.getByRole('textbox', { name: 'Known words' })
  await commands.takeSentRequests()

  // Act
  await userEvent.fill(input, 'radancy')
  await userEvent.keyboard('{Enter}')
  await expect.element(screen.getByRole('alert')).toHaveTextContent('“Radancy” is already a known word.')
  await userEvent.fill(input, 'grdb')
  await userEvent.keyboard('{Enter}')

  // Assert
  await expect.element(screen.getByRole('alert')).toHaveTextContent('“GRDB” is already in your corrections below. Edit it there.')
  await expect.element(input).toHaveValue('grdb')
  await expect.element(input).toHaveAttribute('aria-invalid', 'true')
  expect(await commands.takeSentRequests()).toEqual([])
})

test('a known word that the Server refuses shows its message under the input and keeps the word', async () => {
  // Arrange
  const refused = {
    type: 'https://tools.ietf.org/html/rfc9110#section-15.5.21',
    title: 'One or more validation errors occurred.',
    status: 422,
    errors: { canonicalTerm: ['The term must not have more than 200 characters.'] },
  }
  const long = 'W'.repeat(201)
  const { screen } = await renderPageAt('/corrections', [{ method: 'POST', path: '/api/v1/corrections', status: 422, body: refused }])
  const input = screen.getByRole('textbox', { name: 'Known words' })
  await userEvent.fill(input, long)

  // Act
  await userEvent.keyboard('{Enter}')

  // Assert
  await expect.element(screen.getByRole('alert')).toHaveTextContent('The term must not have more than 200 characters.')
  await expect.element(input).toHaveValue(long)
  await expect.element(input).toHaveAccessibleDescription(
    'Cleanup and cloud dictation use these words to spell names and terms. The term must not have more than 200 characters.',
  )
})

test('a known word that fails with no message of the Server shows a fixed message and keeps the word', async () => {
  // Arrange
  const { screen } = await renderPageAt('/corrections', [{ method: 'POST', path: '/api/v1/corrections', status: 409, body: staleVersion }])
  const input = screen.getByRole('textbox', { name: 'Known words' })
  await userEvent.fill(input, 'Tallinn')

  // Act
  await userEvent.keyboard('{Enter}')

  // Assert
  await expect.element(screen.getByRole('alert')).toHaveTextContent('The word was not saved. Try again.')
  await expect.element(input).toHaveValue('Tallinn')
})

test('a known word that gets an answer that is not a problem shows the words of the app, not of the answer', async () => {
  // Arrange
  const { screen } = await renderPageAt('/corrections', [
    { method: 'POST', path: '/api/v1/corrections', status: 502, contentType: 'text/html', body: '<html>Bad gateway</html>' },
  ])
  const input = screen.getByRole('textbox', { name: 'Known words' })
  await userEvent.fill(input, 'Tallinn')

  // Act
  await userEvent.keyboard('{Enter}')

  // Assert
  await expect.element(screen.getByRole('alert')).toHaveTextContent(
    'The Server could not do this, and it answered with error 502. Try again later.',
  )
})

test('the input keeps the focus when the list fails to load again after an add', async () => {
  // Arrange
  const tallinn: CorrectionDto = { id: 'cor_5', canonicalTerm: 'Tallinn', mishearings: [], hardReplace: false, version: 1 }
  const { screen } = await renderPageAt('/corrections')
  const input = screen.getByRole('textbox', { name: 'Known words' })
  await userEvent.fill(input, 'Tallinn')
  await commands.answerRequests([
    ...signedInMember,
    { method: 'GET', path: '/api/v1/corrections', status: 403, body: { title: 'Forbidden', status: 403 } },
    { method: 'POST', path: '/api/v1/corrections', status: 201, body: tallinn },
  ])

  // Act
  await userEvent.keyboard('{Enter}')

  // Assert
  await expect.element(screen.getByText('Your corrections did not load.')).toBeVisible()
  await expect.element(input).toBeEnabled()
  await expect.element(input).toHaveFocus()
})

test('a second Enter while a known word is saved sends it once', async () => {
  // Arrange
  const tallinn: CorrectionDto = { id: 'cor_5', canonicalTerm: 'Tallinn', mishearings: [], hardReplace: false, version: 1 }
  const { screen } = await renderPageAt('/corrections', [{ method: 'POST', path: '/api/v1/corrections', status: 201, body: tallinn }])
  const input = screen.getByRole('textbox', { name: 'Known words' })
  await userEvent.fill(input, 'Tallinn')
  await commands.takeSentRequests()
  await commands.holdRequests('/api/v1/corrections')

  // Act
  await userEvent.keyboard('{Enter}')
  await expect.element(input).toHaveAttribute('readonly')
  await userEvent.keyboard('{Enter}')
  await commands.releaseRequests('/api/v1/corrections')

  // Assert
  await expect.element(input).toHaveValue('')
  const sent = await commands.takeSentRequests()
  expect(sent.filter((request) => request.method === 'POST')).toHaveLength(1)
})

test('the × of a pill removes that known word at its version, and the focus goes to the input', async () => {
  // Arrange
  const { screen } = await renderPageAt('/corrections')
  await expect.element(screen.getByRole('button', { name: 'Remove Radancy' })).toBeVisible()
  await commands.answerRequests([
    ...signedInMember,
    { ...listAnswer, body: [cafe, grdb] },
    { method: 'DELETE', path: '/api/v1/corrections/cor_4', query: { ifVersion: '1' }, status: 204 },
  ])

  // Act
  await userEvent.click(screen.getByRole('button', { name: 'Remove Radancy' }))

  // Assert
  await expect.element(screen.getByRole('button', { name: 'Remove Radancy' })).not.toBeInTheDocument()
  await expect.element(screen.getByRole('textbox', { name: 'Known words' })).toHaveFocus()
  await expect.element(screen.getByRole('status').filter({ hasText: 'Removed Radancy.' })).toBeInTheDocument()
})

test('the dialog refuses a correction with no mishearing and sends nothing', async () => {
  // Arrange
  const { screen } = await renderPageAt('/corrections')
  await userEvent.click(screen.getByRole('button', { name: 'Add correction', exact: true }))
  const dialog = screen.getByRole('dialog')
  await userEvent.fill(dialog.getByRole('textbox', { name: 'Term' }), 'Tallinn')
  await commands.takeSentRequests()

  // Act
  await userEvent.click(dialog.getByRole('button', { name: 'Save correction' }))

  // Assert
  await expect
    .element(dialog.getByText('Please provide at least one mishearing. To keep a word with no mishearings, add it under Known words.'))
    .toBeVisible()
  await expect.element(dialog.getByRole('textbox', { name: 'Mishearings' })).toHaveFocus()
  expect(await commands.takeSentRequests()).toEqual([])
})

test('an edit that clears the mishearings makes the term a known word, and says so', async () => {
  // Arrange
  const known: CorrectionDto = { ...grdb, mishearings: [], version: 2 }
  const { screen } = await renderPageAt('/corrections')
  await userEvent.click(screen.getByRole('button', { name: 'Edit GRDB' }))
  const dialog = screen.getByRole('dialog')
  await userEvent.clear(dialog.getByRole('textbox', { name: 'Mishearings' }))
  await commands.answerRequests([
    ...signedInMember,
    { ...listAnswer, body: [cafe, known, radancy] },
    { method: 'PUT', path: '/api/v1/corrections/cor_1', body: known },
  ])
  await commands.takeSentRequests()

  // Act
  await userEvent.click(dialog.getByRole('button', { name: 'Save correction' }))

  // Assert
  await expect.element(screen.getByRole('alertdialog').filter({ hasText: '“GRDB” is now a known word.' })).toBeVisible()
  await expect.element(screen.getByRole('button', { name: 'Remove GRDB' })).toBeVisible()
  await expect.element(screen.getByRole('button', { name: 'Edit GRDB' })).not.toBeInTheDocument()
  await expect.element(screen.getByRole('heading', { level: 1, name: 'Corrections' })).toHaveFocus()
  const sent = await commands.takeSentRequests()
  expect(sent.filter((request) => request.method === 'PUT')).toEqual([
    { method: 'PUT', path: '/api/v1/corrections/cor_1', body: { canonicalTerm: 'GRDB', mishearings: [], hardReplace: false, version: 1 } },
  ])
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
  await userEvent.fill(dialog.getByRole('textbox', { name: 'Mishearings' }), 'tall in')
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
  await userEvent.fill(dialog.getByRole('textbox', { name: 'Mishearings' }), 'tall in')
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
  await userEvent.fill(dialog.getByRole('textbox', { name: 'Mishearings' }), 'smile')
  await commands.takeSentRequests()

  // Act
  await userEvent.click(dialog.getByRole('button', { name: 'Save correction' }))

  // Assert
  await expect.element(screen.getByRole('dialog')).not.toBeInTheDocument()
  const [sent] = await commands.takeSentRequests()
  expect(sent?.body).toMatchObject({ canonicalTerm: term })
})
