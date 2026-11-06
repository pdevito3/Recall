import { beforeEach, expect, test, vi } from 'vitest'

beforeEach(() => {
  vi.resetModules()
})

async function freshPage() {
  const pageUser = await import('./page-user')
  const onMismatch = vi.fn()
  pageUser.setPageUserMismatchHandler(onMismatch)
  return { ...pageUser, onMismatch }
}

test('a page has no person until the first answer, and that person stays', async () => {
  const { pageUserOf, notePageUser, onMismatch } = await freshPage()

  expect(pageUserOf()).toBeNull()
  notePageUser('person-a')
  notePageUser('person-a')

  expect(pageUserOf()).toBe('person-a')
  expect(onMismatch).not.toHaveBeenCalled()
})

test('an answer with another person leaves the page, and the page keeps its first person', async () => {
  const { pageUserOf, notePageUser, onMismatch } = await freshPage()

  notePageUser('person-a')
  notePageUser('person-b')

  expect(onMismatch).toHaveBeenCalledOnce()
  expect(pageUserOf()).toBe('person-a')
})
