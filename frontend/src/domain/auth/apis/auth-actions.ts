import { useMutation, type QueryClient } from '@tanstack/react-query'

import { Notification } from '@/components/notification'
import { apiClient } from '@/lib/api-client'
import { authUserQuery, getUser } from './get-user'

import { settleUnsentWords } from './unsent-words-at-logout'

const serverMaxReturnPathLength = 2048

const signedOutPage = '/auth/login?signedOut=1'

const restoredPageCheckLimitMs = 3000

type LogoutAnswer = { provider_logout_url: string } | undefined

/** A call that answers 401 after the logout starts a login, and a navigation with a return URL would abort the one that the logout started. */
let afterSignOut: string | undefined

/** A call that answers 401 while the logout runs also starts a login, and that navigation would lose the answer of the logout. */
let logoutInFlight: Promise<unknown> | undefined

let otherTabs: BroadcastChannel | undefined

let sessionOfHiddenPage: string | undefined

let newestRestore = 0

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
  const signingOut = settleUnsentWordsAndSignOut()
  const running = Promise.allSettled([logoutInFlight, signingOut])
  logoutInFlight = running
  try {
    await signingOut
  } finally {
    if (logoutInFlight === running) logoutInFlight = undefined
  }
}

async function settleUnsentWordsAndSignOut() {
  const leaveNoSign = await settleUnsentWords()
  const { data } = await apiClient.post<LogoutAnswer>('/auth/logout', undefined, { suppressErrorToast: true })
  leaveNoSign()
  afterSignOut = data?.provider_logout_url ?? afterSignOut ?? signedOutPage
  otherTabs?.postMessage('signed-out')
  hidePage()
  window.location.replace(afterSignOut)
}

/**
 * Opacity and not visibility: `visibility` inherits, and an element with `transition-all` would keep showing it for the
 * time of its transition. Opacity leaves the text to a screen reader, to a search in the page, and to the keyboard, so
 * `inert` closes those.
 */
function hidePage() {
  document.documentElement.style.opacity = '0'
  document.documentElement.inert = true
}

function showPage() {
  document.documentElement.style.opacity = ''
  document.documentElement.inert = false
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

/**
 * A page from the back-forward cache runs no first load, so nothing else checks that its session still lives. A check
 * can outlive its restore in a frozen page, so only the check of the newest restore acts.
 */
async function checkSessionOfRestoredPage(queryClient: QueryClient) {
  const restore = ++newestRestore
  const noAnswer = new Promise<undefined>((settle) => setTimeout(settle, restoredPageCheckLimitMs))
  const user = await Promise.race([getUser().catch(() => undefined), noAnswer])
  if (restore !== newestRestore) return
  if (user === null) leaveAsSignedOut(queryClient)
  else if (user && user.sid === sessionOfHiddenPage) showPage()
  // A new load shows the alert of a failed check, or the pages of the session that lives now, and the words of this page stay hidden.
  else window.location.reload()
}

/**
 * Keeps private text off the screen when the session of a page ends while the page cannot know it: another tab signs out,
 * or the page waits in the back-forward cache. The page hides as it enters that cache, and only an answer of a live session
 * shows it again. Each answer of the Server has `no-store`, and with that Chromium did not restore a page, so the part of this
 * guard for that cache acts in a browser that still restores, as a WKWebView did. docs/authentication.md has the runs.
 */
export function guardPageAgainstEndedSession(queryClient: QueryClient) {
  otherTabs = new BroadcastChannel('recall.session')
  otherTabs.onmessage = () => leaveAsSignedOut(queryClient)
  window.addEventListener('pagehide', (event) => {
    if (!event.persisted) return
    hidePage()
    // Read here, because a refetch during a restore can already hold the session of the next person.
    sessionOfHiddenPage = queryClient.getQueryData(authUserQuery.queryKey)?.sid
  })
  window.addEventListener('pageshow', (event) => {
    if (event.persisted) void checkSessionOfRestoredPage(queryClient)
  })
}

export function useLogout() {
  return useMutation({
    mutationFn: logout,
    // By default a mutation waits with no sign while the browser has no network, and the logout must ask about unsent words now.
    networkMode: 'always',
    onError: () => Notification.error('You are still signed in, because signing out did not finish. Try again.'),
  })
}
