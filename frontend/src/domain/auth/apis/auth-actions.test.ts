import { QueryClient } from '@tanstack/react-query'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { returnUrlOf } from './auth-actions'

const at = (pathname: string, search = '', hash = '') => ({ pathname, search, hash })

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
    vi.stubGlobal('window', { location: { replace } })
    vi.stubGlobal('document', { documentElement: { style: {} } })
    vi.resetModules()
    const { apiClient } = await import('@/lib/api-client')
    const post = vi.spyOn(apiClient, 'post').mockResolvedValue({ status: 204 })
    return { replace, post, ...(await import('./auth-actions')) }
  }

  afterEach(() => {
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
  })

  it('goes to the signed-out page, because a stopped navigation leaves a document that still calls the API', async () => {
    const { replace, login, logout } = await freshActions()

    await logout()
    replace.mockClear()
    login('/about')

    expect(replace).toHaveBeenCalledExactlyOnceWith('/auth/login?signedOut=1')
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
    login('/about')
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
    const listeners = new Map<string, (event: { persisted: boolean }) => void>()
    const addEventListener = (type: string, listener: (event: { persisted: boolean }) => void) => listeners.set(type, listener)
    vi.stubGlobal('window', { location: { replace, reload }, addEventListener })
    vi.stubGlobal('document', { documentElement: { style: {} } })
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
    return { replace, reload, post, get, queryClient, userKey, hidePage, restorePage, channel: channels[0]!, ...actions }
  }

  const answerWithUser = (sid: string) => ({ status: 200, data: { sub: '1', name: 'A Person', sid, client_kind: 'web' } })
  const opacityOfPage = () => document.documentElement.style.opacity

  it('compares with the session at the time that the page was hidden, because a refetch can bring the session of the next person', async () => {
    const { reload, get, queryClient, userKey, hidePage, restorePage } = await guardedActions()
    queryClient.setQueryData(userKey, answerWithUser('first').data as never)
    hidePage()
    queryClient.setQueryData(userKey, answerWithUser('second').data as never)
    get.mockResolvedValue(answerWithUser('second'))

    restorePage()

    await vi.waitFor(() => expect(reload).toHaveBeenCalledOnce())
    expect(opacityOfPage()).toBe('0')
  })

  it('shows the page again when the session of the page lives', async () => {
    const { reload, get, queryClient, userKey, hidePage, restorePage } = await guardedActions()
    queryClient.setQueryData(userKey, answerWithUser('first').data as never)
    hidePage()
    expect(document.documentElement.inert).toBe(true)
    get.mockResolvedValue(answerWithUser('first'))

    restorePage()

    await vi.waitFor(() => expect(opacityOfPage()).toBe(''))
    expect(document.documentElement.inert).toBe(false)
    expect(reload).not.toHaveBeenCalled()
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
  })

  it('hides the page, clears the data of this tab, and goes to the signed-out page', async () => {
    const { replace, queryClient, channel } = await guardedActions()
    queryClient.setQueryData(['private'], 'words of this person')

    channel.onmessage!({ data: 'signed-out' })

    expect(document.documentElement.style.opacity).toBe('0')
    expect(queryClient.getQueryCache().getAll()).toHaveLength(0)
    expect(replace).toHaveBeenCalledExactlyOnceWith('/auth/login?signedOut=1')
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
