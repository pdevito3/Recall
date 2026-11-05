import { useMutation } from '@tanstack/react-query'

import { Notification } from '@/components/notification'
import { apiClient } from '@/lib/api-client'

/**
 * Auth action functions
 */

/** The Server ignores a longer return URL, and goes to `/` (`LocalReturnPath`). */
const maxReturnUrlLength = 2048

/**
 * The path, the query, and the hash of the page, as a return URL that the Server accepts. The Server refuses a path that
 * starts with `//` or `/\`. The browser percent-encodes each non-ASCII character of a location, so the length is the
 * length that the Server measures.
 */
export function returnUrlOf({ pathname, search, hash }: Pick<Location, 'pathname' | 'search' | 'hash'>): string {
  if (pathname.startsWith('//') || pathname.startsWith('/\\')) return '/'
  return [pathname + search + hash, pathname].find((url) => url.length <= maxReturnUrlLength) ?? '/'
}

/** Replaces the page in the history, so that Back from the login of the provider does not come back to a page that goes to the login again. */
export function login(returnUrl: string = '/'): void {
  window.location.replace(`/auth/login?returnUrl=${encodeURIComponent(returnUrl)}`)
}

/** The signed-out page reads the mark that this request sets, so a failed request cannot go there and say that the session ended. */
export async function logout(): Promise<void> {
  await apiClient.post('/auth/logout', undefined, { suppressErrorToast: true })
  window.location.replace('/auth/login?signedOut=1')
}

export function useLogout() {
  return useMutation({
    mutationFn: logout,
    onError: () => Notification.error('You are still signed in, because the sign-out did not finish. Try again.'),
  })
}
