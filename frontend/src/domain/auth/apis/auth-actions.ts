import { useMutation } from '@tanstack/react-query'

import { Notification } from '@/components/notification'
import { apiClient } from '@/lib/api-client'

import { settleUnsentWords } from './unsent-words-at-logout'

const serverMaxReturnPathLength = 2048

const signedOutPage = '/auth/login?signedOut=1'

/** A call that answers 401 after the logout starts a login, and a navigation with a return URL would abort the one to the signed-out page. */
let hasSignedOut = false

/**
 * The path, the query, and the hash of the page, as a return URL that the Server accepts. The Server refuses a path that
 * starts with `//` or `/\`. The browser percent-encodes each non-ASCII character of a location, so the length is the
 * length that the Server measures.
 */
export function returnUrlOf({ pathname, search, hash }: Pick<Location, 'pathname' | 'search' | 'hash'>): string {
  if (pathname.startsWith('//') || pathname.startsWith('/\\')) return '/'
  return [pathname + search + hash, pathname].find((url) => url.length <= serverMaxReturnPathLength) ?? '/'
}

/** Replaces the page in the history, so that Back from the login of the provider does not come back to a page that goes to the login again. */
export function login(returnUrl: string = '/'): void {
  window.location.replace(hasSignedOut ? signedOutPage : `/auth/login?returnUrl=${encodeURIComponent(returnUrl)}`)
}

export async function logout(): Promise<void> {
  const leaveNoSign = await settleUnsentWords()
  await apiClient.post('/auth/logout', undefined, { suppressErrorToast: true })
  leaveNoSign()
  hasSignedOut = true
  window.location.replace(signedOutPage)
}

export function useLogout() {
  return useMutation({
    mutationFn: logout,
    onError: () => Notification.error('You are still signed in, because the sign-out did not finish. Try again.'),
  })
}
