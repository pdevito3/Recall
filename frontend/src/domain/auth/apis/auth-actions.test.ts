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

  it('keeps the return URL when the logout fails, because the session may still live', async () => {
    const { replace, login, logout, post } = await freshActions()
    post.mockRejectedValue(new Error('no answer'))

    await expect(logout()).rejects.toThrow('no answer')
    login('/about')

    expect(replace).toHaveBeenCalledExactlyOnceWith('/auth/login?returnUrl=%2Fabout')
  })

  it('keeps the return URL when nobody signed out', async () => {
    const { replace, login } = await freshActions()

    login('/about')

    expect(replace).toHaveBeenCalledExactlyOnceWith('/auth/login?returnUrl=%2Fabout')
  })
})
