import { afterEach, beforeEach, expect, test, vi } from 'vitest'

beforeEach(() => {
  vi.resetModules()
  const stored = new Map<string, string>()
  vi.stubGlobal('localStorage', {
    getItem: (key: string) => stored.get(key) ?? null,
    setItem: (key: string, value: string) => stored.set(key, value),
    removeItem: (key: string) => stored.delete(key),
  })
})

afterEach(() => {
  vi.unstubAllGlobals()
})

function countTheMessages() {
  const postMessage = vi.fn()
  vi.stubGlobal('BroadcastChannel', function (this: { postMessage: typeof postMessage; close: () => void }) {
    this.postMessage = postMessage
    this.close = () => undefined
  })
  return postMessage
}

test('a tab that the person sees again leaves when another person signed in while the tab slept', async () => {
  countTheMessages()
  const page = await freshPage()
  page.notePageUser('person-a')
  localStorage.setItem('recall.lastPerson', 'person-b')

  page.checkLastPersonOfThisBrowser()

  expect(page.onMismatch).toHaveBeenCalledOnce()
})

test('a tab of the last person stays when the person sees it again', async () => {
  countTheMessages()
  const page = await freshPage()
  page.notePageUser('person-a')

  page.checkLastPersonOfThisBrowser()

  expect(page.onMismatch).not.toHaveBeenCalled()
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

test('a next page of the same person sends no message, so a page in the back-forward cache stays', async () => {
  const messages = countTheMessages()

  ;(await freshPage()).notePageUser('person-a')
  vi.resetModules()
  ;(await freshPage()).notePageUser('person-a')

  expect(messages).toHaveBeenCalledOnce()
})

test('the first page of another person tells the other tabs', async () => {
  const messages = countTheMessages()

  ;(await freshPage()).notePageUser('person-a')
  vi.resetModules()
  ;(await freshPage()).notePageUser('person-b')

  expect(messages).toHaveBeenLastCalledWith({ signedInAs: 'person-b' })
})

test('the first page of another person tells the other tabs with no room in storage', async () => {
  const messages = countTheMessages()
  vi.stubGlobal('localStorage', {
    getItem: () => null,
    setItem: () => {
      throw new DOMException('Full', 'QuotaExceededError')
    },
  })

  ;(await freshPage()).notePageUser('person-b')

  expect(messages).toHaveBeenCalledWith({ signedInAs: 'person-b' })
})

test('a refused write of a person does not hide the return of the earlier person', async () => {
  const messages = countTheMessages()
  ;(await freshPage()).notePageUser('person-a')
  vi.spyOn(localStorage, 'setItem').mockImplementation(() => {
    throw new DOMException('Full', 'QuotaExceededError')
  })
  vi.resetModules()
  ;(await freshPage()).notePageUser('person-b')
  vi.resetModules()

  ;(await freshPage()).notePageUser('person-a')

  expect(messages).toHaveBeenLastCalledWith({ signedInAs: 'person-a' })
})
