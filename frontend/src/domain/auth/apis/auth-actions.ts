import { useMutation, type QueryClient } from '@tanstack/react-query'

import { Notification } from '@/components/notification'
import { apiClient } from '@/lib/api-client'
import { getUser } from './get-user'

const serverMaxReturnPathLength = 2048

const signedOutPage = '/auth/login?signedOut=1'

const restoredPageCheckLimitMs = 3000

type LogoutAnswer = { provider_logout_url: string } | undefined

/** A call that answers 401 after the logout starts a login, and a navigation with a return URL would abort the one that the logout started. */
let afterSignOut: string | undefined

/** A call that answers 401 while the logout runs also starts a login, and that navigation would lose the answer of the logout. */
let logoutInFlight: Promise<unknown> | undefined

let otherTabs: BroadcastChannel | undefined

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
  if (logoutInFlight) {
    void logoutInFlight.then(() => login(returnUrl))
    return
  }
  window.location.replace(afterSignOut ?? `/auth/login?returnUrl=${encodeURIComponent(returnUrl)}`)
}

export async function logout(): Promise<void> {
  const answer = apiClient.post<LogoutAnswer>('/auth/logout', undefined, { suppressErrorToast: true })
  const running = Promise.allSettled([logoutInFlight, answer])
  logoutInFlight = running
  try {
    const { data } = await answer
    afterSignOut = data?.provider_logout_url ?? afterSignOut ?? signedOutPage
    otherTabs?.postMessage('signed-out')
    window.location.replace(afterSignOut)
  } finally {
    if (logoutInFlight === running) logoutInFlight = undefined
  }
}

/** Opacity and not visibility: `visibility` inherits, and an element with `transition-all` would keep showing it for the time of its transition. */
function hidePage() {
  document.documentElement.style.opacity = '0'
}

/**
 * The session of this tab ended elsewhere, so its private text goes before anything else can happen.
 *
 * Rule for the code that holds unsent words (R12 and later): this tab leaves with no wait and no prompt, and its query
 * cache is gone. So unsent words must be in the outbox of their owner, in localStorage, when `pagehide` fires. Write them
 * in a synchronous `pagehide` listener or sooner, and never only in React state or the query cache. A sign-out must not
 * remove them, because the next login of the same owner finds and sends them, and the key names the owner, so another
 * person who signs in on this device cannot read them.
 */
function leaveAsSignedOut(queryClient: QueryClient) {
  hidePage()
  queryClient.clear()
  afterSignOut ??= signedOutPage
  window.location.replace(afterSignOut)
}

/** A page from the back-forward cache runs no first load, so nothing else checks that its session still lives. */
async function checkSessionOfRestoredPage(queryClient: QueryClient) {
  const noAnswer = new Promise<undefined>((settle) => setTimeout(settle, restoredPageCheckLimitMs))
  const user = await Promise.race([getUser().catch(() => undefined), noAnswer])
  if (user === null) leaveAsSignedOut(queryClient)
  // A new load shows the alert of a failed check, and the words of this page stay hidden.
  else if (user === undefined) window.location.reload()
  else document.documentElement.style.opacity = ''
}

/**
 * Keeps private text off the screen when the session of a page ends while the page cannot know it: another tab signs out,
 * or the page waits in the back-forward cache. The page hides as it enters that cache, so that it is hidden at the first
 * paint of a restore, and only an answer of a live session shows it again.
 */
export function guardPageAgainstEndedSession(queryClient: QueryClient) {
  otherTabs = new BroadcastChannel('recall.session')
  otherTabs.onmessage = () => leaveAsSignedOut(queryClient)
  window.addEventListener('pagehide', (event) => {
    if (event.persisted) hidePage()
  })
  window.addEventListener('pageshow', (event) => {
    if (event.persisted) void checkSessionOfRestoredPage(queryClient)
  })
}

export function useLogout() {
  return useMutation({
    mutationFn: logout,
    onError: () => Notification.error('You are still signed in, because the sign-out did not finish. Try again.'),
  })
}
