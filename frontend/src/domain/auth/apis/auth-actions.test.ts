import { QueryClient } from '@tanstack/react-query'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { returnUrlOf } from './auth-actions'

const at = (pathname: string, search = '', hash = '') => ({ pathname, search, hash })

let wordsInMemory: string[] = []
let wordsInAKey: string[] = []

/** Words that a write with no room left only in the memory of this tab, and words in a key, which the dialog of a sign-out lists. */
const holdInMemory = (words: string[], inAKey: string[] = []) => {
  wordsInMemory = words
  wordsInAKey = inAKey
  vi.doMock('@/domain/sessions/autosave/draft-saver', async (original) => ({
    ...(await original<typeof import('@/domain/sessions/autosave/draft-saver')>()),
    wordsOfThisTabOnlyInMemory: () => wordsInMemory,
    unsentWordsOnThisDevice: () => [
      ...wordsInMemory.map((words) => ({ words, isRefused: false, isOnlyInMemory: true, discard: () => undefined })),
      ...wordsInAKey.map((words) => ({ words, isRefused: false, isOnlyInMemory: false, discard: () => undefined })),
    ],
  }))
}

const thisPage = { pathname: '/sessions/ses_1', search: '?channel=raw', hash: '#end' }

const listenersOf = () => {
  const listeners = new Map<string, (event: { persisted: boolean }) => void>()
  return {
    listeners,
    addEventListener: (type: string, listener: (event: { persisted: boolean }) => void) => listeners.set(type, listener),
    removeEventListener: (type: string, listener: unknown) => listeners.get(type) === listener && listeners.delete(type),
  }
}

describe('the return URL of the login', () => {
  it('is the path, the query, and the hash of the page', () => {
    expect(returnUrlOf(at('/about', '?tab=team', '#people'))).toBe('/about?tab=team#people')
  })

  it.each([
    ['//', '//evil.example/x'],
    ['/\\', '/\\evil.example/x'],
  ])('is / for a path that starts with %s, which the Server refuses', (_, pathname) => {
    expect(returnUrlOf(at(pathname, '?a=1'))).toBe('/')
  })

  it('is the path of the page when the path, the query, and the hash are longer than 2048 characters', () => {
    const search = `?q=${'a'.repeat(2048)}`

    expect(returnUrlOf(at('/about', search, '#people'))).toBe('/about')
  })

  it('keeps a return URL of exactly 2048 characters', () => {
    const pathname = `/${'a'.repeat(2047)}`

    expect(returnUrlOf(at(pathname))).toBe(pathname)
  })

  it('is / when the path alone is longer than 2048 characters', () => {
    expect(returnUrlOf(at(`/${'a'.repeat(2048)}`, '?a=1'))).toBe('/')
  })
})

describe('a login after a sign-out', () => {
  const freshActions = async () => {
    const replace = vi.fn()
    const { listeners, addEventListener, removeEventListener } = listenersOf()
    vi.stubGlobal('window', { location: { replace }, addEventListener, removeEventListener })
    vi.stubGlobal('document', { documentElement: { style: {} } })
    vi.resetModules()
    const { apiClient } = await import('@/lib/api-client')
    const post = vi.spyOn(apiClient, 'post').mockResolvedValue({ status: 204 })
    const { leftTheApp } = await import('./leave-the-app')
    const { LeftTheAppFirst } = await import('./unsent-words-at-logout')
    return { replace, post, listeners, leftTheApp, LeftTheAppFirst, ...(await import('./auth-actions')) }
  }

  /** The dialog of the sign-out shows at the first note of the work that it waits for. */
  const questionAsked = async () => {
    const { toastQueue } = await import('@/components/notification')
    await vi.waitFor(() => expect(toastQueue.visibleToasts).toEqual([]), { timeout: 8000 })
  }

  it('closes the question of a sign-out when the session ends meanwhile, and shows the words that only the memory holds at once', { timeout: 15_000 }, async () => {
    holdInMemory(['words in memory'])
    const { replace, post, leftTheApp, LeftTheAppFirst, login, logout } = await freshActions()
    const signingOut = logout()
    await questionAsked()

    login('/about')

    await expect(signingOut).rejects.toBeInstanceOf(LeftTheAppFirst)
    expect(leftTheApp()).toMatchObject({ why: 'sessionEnded', words: ['words in memory'] })
    expect(post).not.toHaveBeenCalled()
    expect(replace).not.toHaveBeenCalled()
  })

  it('leaves at once when the session ends while the question of a sign-out waits on words in a key, which wait for the next login', { timeout: 15_000 }, async () => {
    holdInMemory([], ['words in a key'])
    const { replace, post, LeftTheAppFirst, login, logout } = await freshActions()
    const signingOut = logout()
    await questionAsked()

    login('/about')

    expect(replace).toHaveBeenCalledExactlyOnceWith('/auth/login?returnUrl=%2Fabout')
    await expect(signingOut).rejects.toBeInstanceOf(LeftTheAppFirst)
    expect(post).not.toHaveBeenCalled()
  })

  afterEach(() => {
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
    vi.doUnmock('@/domain/sessions/autosave/draft-saver')
  })

  it('shows the words that only the memory holds after the logout, also words typed while it ran, and leaves only at Leave', async () => {
    holdInMemory([])
    const { replace, post, listeners, leftTheApp, logout } = await freshActions()
    post.mockImplementation(async () => {
      wordsInMemory = ['typed while the logout ran']
      return { status: 204 }
    })

    await logout()

    expect(replace).not.toHaveBeenCalled()
    expect(leftTheApp()).toMatchObject({ why: 'signedOut', words: ['typed while the logout ran'] })
    expect(document.documentElement.style.opacity).not.toBe('0')
    expect(listeners.has('beforeunload')).toBe(true)
    const left = leftTheApp()
    if (left?.why === 'anotherPerson') throw new Error('expected the screen of words')
    left?.leave()
    expect(listeners.has('beforeunload')).toBe(false)
    expect(replace).toHaveBeenCalledExactlyOnceWith('/auth/login?signedOut=1')
  })

  it('shows the words that only the memory holds when the session ends with no message, and keeps the way back', async () => {
    holdInMemory(['words in memory'])
    const { replace, leftTheApp, login } = await freshActions()

    login('/about')
    login('/about')

    expect(replace).not.toHaveBeenCalled()
    expect(leftTheApp()).toMatchObject({ why: 'sessionEnded', words: ['words in memory'] })
    const left = leftTheApp()
    if (left?.why === 'anotherPerson') throw new Error('expected the screen of words')
    left?.leave()
    expect(replace).toHaveBeenCalledExactlyOnceWith('/auth/login?returnUrl=%2Fabout')
  })

  it('goes to the signed-out page, because a stopped navigation leaves a document that still calls the API', async () => {
    const { replace, login, logout } = await freshActions()

    await logout()
    replace.mockClear()
    login('/about')

    expect(replace).toHaveBeenCalledExactlyOnceWith('/auth/login?signedOut=1')
  })

  it('shows that the app works at once, and stops when the page leaves', async () => {
    const { logout, post } = await freshActions()
    const { toastQueue } = await import('@/components/notification')
    let answer: (value: { status: number }) => void = () => undefined
    post.mockReturnValue(new Promise((resolve) => (answer = resolve)))

    const running = logout()

    expect(toastQueue.visibleToasts.map(({ content }) => content.message)).toEqual(['Signing out'])
    await vi.waitFor(() => expect(post).toHaveBeenCalled(), { timeout: 3000 })
    answer({ status: 204 })
    await running
    expect(toastQueue.visibleToasts).toEqual([])
  })

  const providerLogoutUrl = 'https://idp.test/logout?client_id=recall&post_logout_redirect_uri=https%3A%2F%2Frecall.test%2Fauth%2Fsigned-out'

  it('goes to the logout of the provider when the Server names one', async () => {
    const { replace, logout, post } = await freshActions()
    post.mockResolvedValue({ status: 200, data: { provider_logout_url: providerLogoutUrl } })

    await logout()

    expect(replace).toHaveBeenCalledExactlyOnceWith(providerLogoutUrl)
  })

  it('goes to the signed-out page when the Server names no logout of a provider', async () => {
    const { replace, logout } = await freshActions()

    await logout()

    expect(replace).toHaveBeenCalledExactlyOnceWith('/auth/login?signedOut=1')
  })

  it('keeps the way to the logout of the provider, because a login there would sign the same person in again', async () => {
    const { replace, login, logout, post } = await freshActions()
    post.mockResolvedValue({ status: 200, data: { provider_logout_url: providerLogoutUrl } })

    await logout()
    replace.mockClear()
    login('/about')

    expect(replace).toHaveBeenCalledExactlyOnceWith(providerLogoutUrl)
  })

  it('keeps the way to the logout of the provider after a second click, which has no session left to name a provider', async () => {
    const { replace, logout, post } = await freshActions()
    post.mockResolvedValueOnce({ status: 200, data: { provider_logout_url: providerLogoutUrl } })

    await logout()
    await logout()

    expect(replace).toHaveBeenCalledTimes(2)
    expect(replace).toHaveBeenLastCalledWith(providerLogoutUrl)
  })

  it('waits for the answer of a running logout, because a login now would lose it', async () => {
    const { replace, login, logout, post } = await freshActions()
    let resolve!: (answer: unknown) => void
    post.mockReturnValue(new Promise((settle) => (resolve = settle)))

    const signingOut = logout()
    await vi.waitFor(() => expect(post).toHaveBeenCalled(), { timeout: 3000 })
    login('/about')
    expect(replace).not.toHaveBeenCalled()
    resolve({ status: 200, data: { provider_logout_url: providerLogoutUrl } })
    await signingOut
    await vi.waitFor(() => expect(replace).toHaveBeenCalledTimes(2))

    expect(replace).toHaveBeenNthCalledWith(1, providerLogoutUrl)
    expect(replace).toHaveBeenNthCalledWith(2, providerLogoutUrl)
  })

  it('keeps the return URL of a login that waited for a logout that fails', async () => {
    const { replace, login, logout, post } = await freshActions()
    let reject!: (cause: Error) => void
    post.mockReturnValue(new Promise((_, fail) => (reject = fail)))

    const signingOut = logout()
    await vi.waitFor(() => expect(post).toHaveBeenCalled(), { timeout: 3000 })
    login('/about')
    expect(replace).not.toHaveBeenCalled()
    reject(new Error('no answer'))
    await expect(signingOut).rejects.toThrow('no answer')

    await vi.waitFor(() => expect(replace).toHaveBeenCalledExactlyOnceWith('/auth/login?returnUrl=%2Fabout'))
  })

  it('keeps the return URL when the logout fails, because the session may still live', async () => {
    const { replace, login, logout, post } = await freshActions()
    post.mockRejectedValue(new Error('no answer'))

    await expect(logout()).rejects.toThrow('no answer')
    login('/about')

    expect(replace).toHaveBeenCalledExactlyOnceWith('/auth/login?returnUrl=%2Fabout')
  })

  it('hides the page before it leaves, because the next page can take long or the person can stop it', async () => {
    const { logout } = await freshActions()

    await logout()

    expect(document.documentElement.style.opacity).toBe('0')
    expect(document.documentElement.inert).toBe(true)
  })

  it('keeps the page when the logout fails', async () => {
    const { logout, post } = await freshActions()
    post.mockRejectedValue(new Error('no answer'))

    await expect(logout()).rejects.toThrow('no answer')

    expect(document.documentElement.style.opacity).toBeUndefined()
  })

  it('keeps the return URL when nobody signed out', async () => {
    const { replace, login } = await freshActions()

    login('/about')

    expect(replace).toHaveBeenCalledExactlyOnceWith('/auth/login?returnUrl=%2Fabout')
  })

  it('still waits for the second of two running logouts when the first one fails', async () => {
    const { replace, login, logout, post } = await freshActions()
    let failFirst!: (cause: Error) => void
    let answerSecond!: (answer: unknown) => void
    post.mockReturnValueOnce(new Promise((_, fail) => (failFirst = fail)))
    post.mockReturnValueOnce(new Promise((settle) => (answerSecond = settle)))

    const first = logout()
    const second = logout()
    await vi.waitFor(() => expect(post).toHaveBeenCalledTimes(2))
    failFirst(new Error('no answer'))
    await expect(first).rejects.toThrow('no answer')
    login('/about')
    await new Promise((settled) => setTimeout(settled))
    expect(replace).not.toHaveBeenCalled()

    answerSecond({ status: 200, data: { provider_logout_url: providerLogoutUrl } })
    await second
    await vi.waitFor(() => expect(replace).toHaveBeenCalledTimes(2))
    expect(replace).toHaveBeenNthCalledWith(2, providerLogoutUrl)
  })
})

describe('a sign-out in another tab', () => {
  const channels: { postMessage: ReturnType<typeof vi.fn>; close: () => void; onmessage?: (event: { data: unknown }) => void }[] = []

  const guardedActions = async () => {
    channels.length = 0
    const replace = vi.fn()
    const reload = vi.fn()
    const { listeners, addEventListener, removeEventListener } = listenersOf()
    vi.stubGlobal('window', { location: { replace, reload, ...thisPage }, addEventListener, removeEventListener })
    vi.stubGlobal('document', { documentElement: { style: {} }, addEventListener: () => undefined })
    vi.stubGlobal('BroadcastChannel', function (this: (typeof channels)[number]) {
      this.postMessage = vi.fn()
      this.close = vi.fn()
      channels.push(this)
    })
    vi.resetModules()
    const { apiClient } = await import('@/lib/api-client')
    const post = vi.spyOn(apiClient, 'post').mockResolvedValue({ status: 204 })
    const get = vi.spyOn(apiClient, 'get')
    const queryClient = new QueryClient()
    const actions = await import('./auth-actions')
    actions.guardPageAgainstEndedSession(queryClient)
    const userKey = (await import('./get-user')).authUserQuery.queryKey
    const hidePage = () => listeners.get('pagehide')!({ persisted: true })
    const restorePage = () => listeners.get('pageshow')!({ persisted: true })
    const { leftTheApp } = await import('./leave-the-app')
    return { replace, reload, post, get, queryClient, userKey, hidePage, restorePage, leftTheApp, channel: channels[0]!, ...actions }
  }

  const answerWithUser = (sid: string) => ({ status: 200, data: { sub: '1', name: 'A Person', sid, client_kind: 'web' } })
  const opacityOfPage = () => document.documentElement.style.opacity

  it('compares with the session at the time that the page was hidden, because a refetch can bring the session of the next person', async () => {
    const { replace, reload, get, queryClient, userKey, hidePage, restorePage } = await guardedActions()
    queryClient.setQueryData(userKey, answerWithUser('first').data as never)
    hidePage()
    queryClient.setQueryData(userKey, answerWithUser('second').data as never)
    get.mockResolvedValue(answerWithUser('second'))

    restorePage()

    await vi.waitFor(() => expect(replace).toHaveBeenCalledExactlyOnceWith('/sessions/ses_1?channel=raw'))
    expect(opacityOfPage()).toBe('0')
    expect(reload).not.toHaveBeenCalled()
  })

  it('shows the words that only the memory holds when a restored page finds a newer session of the same person, and loads again at Leave', async () => {
    holdInMemory(['words in memory'])
    const { replace, reload, get, queryClient, userKey, hidePage, restorePage, leftTheApp } = await guardedActions()
    queryClient.setQueryData(userKey, answerWithUser('first').data as never)
    hidePage()
    get.mockResolvedValue(answerWithUser('second'))

    restorePage()

    await vi.waitFor(() => expect(leftTheApp()).toMatchObject({ why: 'sessionEnded', words: ['words in memory'] }))
    expect(opacityOfPage()).toBe('')
    const left = leftTheApp()
    if (left?.why === 'anotherPerson') throw new Error('expected the screen of words')
    left?.leave()
    expect(replace).toHaveBeenCalledExactlyOnceWith('/sessions/ses_1?channel=raw')
    expect(reload).not.toHaveBeenCalled()
  })

  it('leaves for another person and says that the words are gone when a restored page finds the session of another person', async () => {
    holdInMemory(['words in memory'])
    const { replace, reload, get, queryClient, userKey, hidePage, restorePage, leftTheApp } = await guardedActions()
    const pageUser = await import('@/lib/page-user')
    const { leaveThePageOfAnotherPerson } = await import('./page-of-another-person')
    pageUser.setPageUserMismatchHandler(() => leaveThePageOfAnotherPerson(queryClient))
    pageUser.notePageUser('1')
    queryClient.setQueryData(userKey, answerWithUser('first').data as never)
    hidePage()
    get.mockResolvedValue({ status: 200, data: { sub: '2', name: 'Another Person', sid: 'of another', client_kind: 'web' } })

    restorePage()

    await vi.waitFor(() => expect(replace).toHaveBeenCalledExactlyOnceWith('/auth/login?anotherPerson=1&wordsLost=1'))
    await new Promise((settled) => setTimeout(settled))
    expect(leftTheApp()).toEqual({ why: 'anotherPerson', hasLostWords: true })
    expect(replace).toHaveBeenCalledOnce()
    expect(reload).not.toHaveBeenCalled()
  })

  it('shows the words that only the memory holds when a restored page gets no answer about its session', async () => {
    holdInMemory(['words in memory'])
    const { reload, get, queryClient, userKey, hidePage, restorePage, leftTheApp } = await guardedActions()
    queryClient.setQueryData(userKey, answerWithUser('first').data as never)
    hidePage()
    get.mockReturnValue(new Promise(() => undefined) as never)

    restorePage()

    await vi.waitFor(() => expect(leftTheApp()).toMatchObject({ why: 'sessionUnchecked', words: ['words in memory'] }), { timeout: 5000 })
    expect(opacityOfPage()).toBe('')
    expect(reload).not.toHaveBeenCalled()
  })

  it('shows the words that only the memory holds when the user query reads null, before the root route unmounts the app', async () => {
    holdInMemory(['words in memory'])
    const { replace, queryClient, userKey, leftTheApp } = await guardedActions()
    queryClient.setQueryData(userKey, answerWithUser('first').data as never)

    queryClient.setQueryData(userKey, null)

    expect(leftTheApp()).toMatchObject({ why: 'sessionEnded', words: ['words in memory'] })
    const left = leftTheApp()
    if (left?.why === 'anotherPerson') throw new Error('expected the screen of words')
    left?.leave()
    expect(replace).toHaveBeenCalledExactlyOnceWith('/auth/login?returnUrl=%2Fsessions%2Fses_1%3Fchannel%3Draw%23end')
  })

  it('goes to the login once when the user query reads null, also at a first load and at a later null', async () => {
    const { replace, queryClient, userKey } = await guardedActions()

    queryClient.setQueryData(userKey, null)
    queryClient.setQueryData(userKey, null)

    expect(replace).toHaveBeenCalledExactlyOnceWith('/auth/login?returnUrl=%2Fsessions%2Fses_1%3Fchannel%3Draw%23end')
  })

  it('shows the page again when the session of the page lives', async () => {
    const { replace, reload, get, queryClient, userKey, hidePage, restorePage } = await guardedActions()
    queryClient.setQueryData(userKey, answerWithUser('first').data as never)
    hidePage()
    expect(document.documentElement.inert).toBe(true)
    get.mockResolvedValue(answerWithUser('first'))

    restorePage()

    await vi.waitFor(() => expect(opacityOfPage()).toBe(''))
    expect(document.documentElement.inert).toBe(false)
    expect(reload).not.toHaveBeenCalled()
    expect(replace).not.toHaveBeenCalled()
  })

  it('ignores the answer of an earlier restore, because the session can have ended since then', async () => {
    const { replace, get, queryClient, userKey, hidePage, restorePage } = await guardedActions()
    queryClient.setQueryData(userKey, answerWithUser('first').data as never)
    let answerFirst!: (answer: unknown) => void
    let answerSecond!: (answer: unknown) => void
    get.mockReturnValueOnce(new Promise((settle) => (answerFirst = settle)) as never)
    get.mockReturnValueOnce(new Promise((settle) => (answerSecond = settle)) as never)
    hidePage()
    restorePage()
    hidePage()
    restorePage()

    answerFirst(answerWithUser('first'))
    await new Promise((settled) => setTimeout(settled))
    expect(opacityOfPage()).toBe('0')

    answerSecond({ status: 204 })
    await vi.waitFor(() => expect(replace).toHaveBeenCalledExactlyOnceWith('/auth/login?signedOut=1'))
  })

  afterEach(() => {
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
    vi.doUnmock('@/domain/sessions/autosave/draft-saver')
  })

  it('hides the page, clears the data of this tab, and goes to the signed-out page', async () => {
    const { replace, queryClient, channel } = await guardedActions()
    queryClient.setQueryData(['private'], 'words of this person')

    channel.onmessage!({ data: 'signed-out' })

    expect(document.documentElement.style.opacity).toBe('0')
    expect(queryClient.getQueryCache().getAll()).toHaveLength(0)
    expect(replace).toHaveBeenCalledExactlyOnceWith('/auth/login?signedOut=1')
  })

  it('shows the words that only its memory holds, alone, and leaves only when the person says so', async () => {
    holdInMemory(['words in memory'])
    const { replace, queryClient, channel, leftTheApp } = await guardedActions()
    queryClient.setQueryData(['private'], 'words of this person')

    channel.onmessage!({ data: 'signed-out' })
    channel.onmessage!({ data: 'signed-out' })

    expect(queryClient.getQueryCache().getAll()).toHaveLength(0)
    expect(leftTheApp()).toMatchObject({ why: 'signedOutInAnotherTab', words: ['words in memory'] })
    expect(opacityOfPage()).not.toBe('0')
    expect(replace).not.toHaveBeenCalled()
    const left = leftTheApp()
    if (left?.why === 'anotherPerson') throw new Error('expected the screen of words')
    left?.leave()
    expect(replace).toHaveBeenCalledExactlyOnceWith('/auth/login?signedOut=1')
  })

  it('shows the page with the words that only its memory holds when a restored page finds its session ended', async () => {
    holdInMemory(['words in memory'])
    const { replace, get, queryClient, userKey, hidePage, restorePage, leftTheApp } = await guardedActions()
    queryClient.setQueryData(userKey, answerWithUser('first').data as never)
    hidePage()
    get.mockResolvedValue({ status: 204 })

    restorePage()

    await vi.waitFor(() => expect(leftTheApp()).toMatchObject({ why: 'sessionEnded', words: ['words in memory'] }))
    expect(opacityOfPage()).toBe('')
    expect(document.documentElement.inert).toBe(false)
    expect(replace).not.toHaveBeenCalled()
  })

  it('drops the words at once when another person signs in, also those of a shown screen, and says that they are gone', async () => {
    holdInMemory(['words in memory'])
    const { replace, queryClient, channel, leftTheApp } = await guardedActions()
    const { leaveThePageOfAnotherPerson } = await import('./page-of-another-person')
    channel.onmessage!({ data: 'signed-out' })
    wordsInMemory = []

    leaveThePageOfAnotherPerson(queryClient)

    expect(leftTheApp()).toEqual({ why: 'anotherPerson', hasLostWords: true })
    expect(replace).toHaveBeenCalledExactlyOnceWith('/auth/login?anotherPerson=1&wordsLost=1')
  })

  it('does not say that words are gone when another person signs in and the tab held none only in memory', async () => {
    holdInMemory([])
    const { replace, queryClient, leftTheApp } = await guardedActions()
    const { leaveThePageOfAnotherPerson } = await import('./page-of-another-person')

    leaveThePageOfAnotherPerson(queryClient)

    expect(leftTheApp()).toEqual({ why: 'anotherPerson', hasLostWords: false })
    expect(replace).toHaveBeenCalledExactlyOnceWith('/auth/login?anotherPerson=1')
  })

  it('tells the other tabs only when the Server answered, because a failed sign-out leaves the session alive', async () => {
    const { logout, post, channel } = await guardedActions()
    post.mockRejectedValueOnce(new Error('no answer'))

    await expect(logout()).rejects.toThrow('no answer')
    expect(channel.postMessage).not.toHaveBeenCalled()

    await logout()
    expect(channel.postMessage).toHaveBeenCalledOnce()
  })
})

describe('a browser with no BroadcastChannel', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('still guards the page, because a tab with no channel must open', async () => {
    vi.stubGlobal('window', { location: { replace: vi.fn(), reload: vi.fn() }, ...listenersOf() })
    vi.stubGlobal('document', { documentElement: { style: {} }, addEventListener: () => undefined })
    vi.stubGlobal('BroadcastChannel', undefined)
    vi.resetModules()
    const actions = await import('./auth-actions')

    expect(() => actions.guardPageAgainstEndedSession(new QueryClient())).not.toThrow()
  })
})
