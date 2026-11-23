import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { createMemoryHistory, createRouter, RouterProvider } from '@tanstack/react-router'
import { afterEach, expect, test } from 'vitest'
import { commands, userEvent } from 'vitest/browser'
import { render } from 'vitest-browser-react'

import type { PersonDto } from '@/api/generated/models/types/person-dto'
import { toastQueue } from '@/components/notification'
import { Toaster } from '@/components/toaster'
import { routeTree } from '@/routeTree.gen'
import type { Answer } from '@/test/answer'
import { signedInMember } from '@/test/session'

const paginationOf = (count: number) => ({
  'x-pagination': JSON.stringify({
    totalCount: count,
    pageSize: 100,
    currentPageSize: count,
    currentStartIndex: 1,
    currentEndIndex: count,
    pageNumber: 1,
    totalPages: 1,
    hasPrevious: false,
    hasNext: false,
  }),
})

const ada: PersonDto = { id: 'per_1', label: 'Ada', version: 1 }
const grace: PersonDto = { id: 'per_2', label: 'Grace', version: 3 }

const listOf = (people: PersonDto[]): Answer => ({ method: 'GET', path: '/api/v1/people', headers: paginationOf(people.length), body: people })

const staleVersion = {
  type: 'urn:recall:problem:stale-version',
  title: 'The record changed after you loaded it.',
  status: 409,
  detail: 'This record changed after you loaded it. Reload it, then make your change again.',
  errors: { version: ['This record changed after you loaded it. Reload it, then make your change again.'] },
}

afterEach(() => toastQueue.clear())

async function renderPeople(people = [ada, grace]) {
  await commands.answerRequests([...signedInMember, listOf(people)])
  const router = createRouter({ routeTree, history: createMemoryHistory({ initialEntries: ['/people'] }) })
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <RouterProvider router={router} />
      <Toaster />
    </QueryClientProvider>,
  )
}

test('a delete sends the version of the row, and the list drops the person', async () => {
  // Arrange
  const screen = await renderPeople()
  await userEvent.click(screen.getByRole('button', { name: 'Delete Grace' }))
  await commands.answerRequests([
    ...signedInMember,
    listOf([ada]),
    { method: 'DELETE', path: '/api/v1/people/per_2', query: { ifVersion: '3' }, status: 204 },
  ])
  await commands.takeSentRequests()

  // Act
  await userEvent.click(screen.getByRole('button', { name: 'Delete person' }))

  // Assert
  await expect.element(screen.getByText('Person deleted')).toBeVisible()
  const list = screen.getByRole('list', { name: 'Your people' })
  await expect.element(list.getByText('Grace')).not.toBeInTheDocument()
  await expect.element(list.getByText('Ada')).toBeVisible()
  await expect.element(screen.getByRole('heading', { level: 1, name: 'People' })).toHaveFocus()
  expect(await commands.takeSentRequests()).toEqual([{ method: 'DELETE', path: '/api/v1/people/per_2', body: null }])
})

test('a delete after a rename keeps the person, shows the message, and shows the new name', async () => {
  // Arrange
  const screen = await renderPeople()
  await userEvent.click(screen.getByRole('button', { name: 'Delete Grace' }))
  await commands.answerRequests([
    ...signedInMember,
    listOf([ada, { ...grace, label: 'Grace Hopper', version: 4 }]),
    { method: 'DELETE', path: '/api/v1/people/per_2', query: { ifVersion: '3' }, status: 409, body: staleVersion },
  ])

  // Act
  await userEvent.click(screen.getByRole('button', { name: 'Delete person' }))

  // Assert
  await expect.element(screen.getByRole('alertdialog').getByText(staleVersion.detail)).toBeVisible()
  const list = screen.getByRole('list', { name: 'Your people' })
  await expect.element(list.getByText('Grace Hopper')).toBeVisible()
  await expect.element(screen.getByText('Person deleted')).not.toBeInTheDocument()
})

const janePark: PersonDto = { id: 'per_3', label: 'Jane', fullName: 'Jane Park', notes: "Sam's teacher", version: 5 }

test('the list shows the full name and the notes under the name', async () => {
  // Arrange and Act
  const screen = await renderPeople([ada, janePark])

  // Assert
  const list = screen.getByRole('list', { name: 'Your people' })
  await expect.element(list.getByText('Jane Park')).toBeVisible()
  await expect.element(list.getByText("Sam's teacher")).toBeVisible()
})

test('a details save sends the version, keeps the name, and the list shows the new details', async () => {
  // Arrange
  const screen = await renderPeople()
  await userEvent.click(screen.getByRole('button', { name: 'Edit the details of Grace' }))
  const saved = { ...grace, fullName: 'Grace Hopper', notes: 'From the navy', version: 4 }
  await commands.answerRequests([
    ...signedInMember,
    listOf([ada, saved]),
    { method: 'PUT', path: '/api/v1/people/per_2/details', body: saved },
  ])
  await userEvent.fill(screen.getByRole('textbox', { name: 'Full name' }), 'Grace Hopper')
  await userEvent.fill(screen.getByRole('textbox', { name: 'Notes' }), ' From the navy ')
  await commands.takeSentRequests()

  // Act
  await userEvent.click(screen.getByRole('button', { name: 'Save details' }))

  // Assert
  await expect.element(screen.getByText('Details saved')).toBeVisible()
  const list = screen.getByRole('list', { name: 'Your people' })
  await expect.element(list.getByText('From the navy')).toBeVisible()
  await expect.element(list.getByText('Grace', { exact: true })).toBeVisible()
  const sent = (await commands.takeSentRequests()).filter((request) => request.method === 'PUT')
  expect(sent).toEqual([
    { method: 'PUT', path: '/api/v1/people/per_2/details', body: { fullName: 'Grace Hopper', notes: 'From the navy', version: 3 } },
  ])
})

test('a stale details save keeps the edits, and Load the latest takes the details that the other save changed', async () => {
  // Arrange
  const screen = await renderPeople()
  await userEvent.click(screen.getByRole('button', { name: 'Edit the details of Grace' }))
  await commands.answerRequests([
    ...signedInMember,
    listOf([ada, grace]),
    { method: 'PUT', path: '/api/v1/people/per_2/details', status: 409, body: staleVersion },
    { method: 'GET', path: '/api/v1/people/per_2', body: { ...grace, fullName: 'Grace B. Hopper', version: 4 } },
  ])
  await userEvent.fill(screen.getByRole('textbox', { name: 'Notes' }), 'From the navy')
  await userEvent.click(screen.getByRole('button', { name: 'Save details' }))
  const dialog = screen.getByRole('dialog')
  await expect.element(dialog.getByText('This person changed after you opened it.')).toBeVisible()

  // Act
  await userEvent.click(dialog.getByRole('button', { name: 'Load the latest' }))

  // Assert
  await expect.element(dialog.getByRole('textbox', { name: 'Full name' })).toHaveValue('Grace B. Hopper')
  await expect.element(dialog.getByRole('textbox', { name: 'Notes' })).toHaveValue('From the navy')
  await expect.element(dialog.getByText(/changed Full name/)).toBeVisible()
  await expect.element(screen.getByText('Details saved')).not.toBeInTheDocument()
})
