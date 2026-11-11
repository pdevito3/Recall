import { hashKey, onlineManager, useMutation, type QueryClient } from '@tanstack/react-query'

import { Notification, toastQueue } from '@/components/notification'
import { apiClient } from '@/lib/api-client'
import { openChannel } from '@/lib/browser-channel'
import { checkLastPersonOfThisBrowser, noteSignInOfAnotherTab, sessionChannelName } from '@/lib/page-user'
import { authUserQuery, getUser } from './get-user'

import { hidePage, leaveTheApp, loginPageFor, showPage, signedOutPage, thisPageAgain } from './leave-the-app'
import { LeftTheAppFirst, settleUnsentWords, StayedSignedIn } from './unsent-words-at-logout'

const serverMaxReturnPathLength = 2048

const restoredPageCheckLimitMs = 3000

const signOutWorkMs = 60_000

type LogoutAnswer = { provider_logout_url: string } | undefined

/** A call that answers 401 after the logout starts a login, and a navigation with a return URL would abort the one that the logout started. */
let afterSignOut: string | undefined

/**
 * A call that answers 401 while the Server signs out also starts a login, and that navigation would lose the answer of
 * the logout. A login does not wait for the question about unsent words: the session ended, so no logout can name a
 * provider, and the way out closes the question.
 */
let logoutInFlight: Promise<unknown> | undefined

let otherTabs: BroadcastChannel | null = null

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
  leaveTheApp({ why: 'sessionEnded', to: afterSignOut ?? loginPageFor(returnUrl) })
}

const showSigningOut = () => Notification.info('Signing out', { description: 'Recall tries to send your last words first. This can take a few seconds.', duration: signOutWorkMs })

export async function logout(): Promise<void> {
  let working = showSigningOut()
  const stopShowingWork = () => toastQueue.close(working)
  try {
    const leaveNoSign = await settleUnsentWords(stopShowingWork, () => {
      working = showSigningOut()
    })
    working = showSigningOut()
    const signingOut = signOutOnTheServer(leaveNoSign)
    const running = Promise.allSettled([logoutInFlight, signingOut])
    logoutInFlight = running
    try {
      await signingOut
    } finally {
      if (logoutInFlight === running) logoutInFlight = undefined
    }
  } finally {
    stopShowingWork()
  }
}

async function signOutOnTheServer(leaveNoSign: () => Promise<void>) {
  const { data } = await apiClient.post<LogoutAnswer>('/auth/logout', undefined, { suppressErrorToast: true })
  await leaveNoSign()
  afterSignOut = data?.provider_logout_url ?? afterSignOut ?? signedOutPage
  otherTabs?.postMessage('signed-out')
  leaveTheApp({ why: 'signedOut', to: afterSignOut })
}

/**
 * The session of this tab ended elsewhere, so its private text goes before anything else can happen.
 *
 * Rule for the code that holds unsent words (R12 and later): this tab leaves with no wait, and its query cache is gone. So
 * unsent words must be in the outbox of their owner, in localStorage, when `pagehide` fires. Write them in a synchronous
 * `pagehide` listener or sooner, and never only in React state or the query cache. A sign-out must not remove them,
 * because the next login of the same owner finds and sends them, and the key names the owner, so another person who
 * signs in on this device cannot read them. Words that a write with no room left only in memory stay on the screen,
 * alone, until the person leaves (`leaveTheApp`).
 */
function leaveAsSignedOut(queryClient: QueryClient, why: 'signedOutInAnotherTab' | 'sessionEnded') {
  afterSignOut ??= signedOutPage
  leaveTheApp({ why, to: afterSignOut })
  queryClient.clear()
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
  if (user === null) leaveAsSignedOut(queryClient, 'sessionEnded')
  else if (user && user.sid === sessionOfHiddenPage) showPage()
  // The session of this page ended, or no answer came. A new load shows the pages of the session that lives now, or the
  // alert of a failed check. getUser already left for another person, and leaveTheApp keeps that way out.
  else leaveTheApp({ why: user ? 'sessionEnded' : 'sessionUnchecked', to: thisPageAgain() })
}

/** The user query of each page reads null when the session ended, also at the first load, and the app goes before the root route shows nothing. */
function leaveWhenTheSessionEnds(queryClient: QueryClient) {
  const userQueryHash = hashKey(authUserQuery.queryKey)
  let user: unknown
  queryClient.getQueryCache().subscribe(({ type, query }) => {
    if (type !== 'updated' || query.queryHash !== userQueryHash) return
    const before = user
    user = query.state.data
    if (user === null && before !== null) login(returnUrlOf(window.location))
  })
}

/**
 * Keeps private text off the screen when the session of a page ends while the page cannot know it: another tab signs out,
 * or the page waits in the back-forward cache. The page hides as it enters that cache, and only an answer of a live session
 * shows it again. Each answer of the Server has `no-store`, and with that Chromium did not restore a page, so the part of this
 * guard for that cache acts in a browser that still restores, as a WKWebView did. docs/authentication.md has the runs.
 */
export function guardPageAgainstEndedSession(queryClient: QueryClient) {
  leaveWhenTheSessionEnds(queryClient)
  otherTabs = openChannel(sessionChannelName)
  if (otherTabs) {
    otherTabs.onmessage = ({ data }: MessageEvent<'signed-out' | { signedInAs: string }>) => {
      if (data === 'signed-out') leaveAsSignedOut(queryClient, 'signedOutInAnotherTab')
      else noteSignInOfAnotherTab(data.signedInAs)
    }
  }
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible') checkLastPersonOfThisBrowser()
  })
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
    onError: (error) => {
      if (error instanceof StayedSignedIn || error instanceof LeftTheAppFirst) return
      Notification.error(
        onlineManager.isOnline()
          ? 'You are still signed in, because signing out did not finish. Try again.'
          : 'You are still signed in, because a sign-out needs the network. Try again when you are online.',
      )
    },
  })
}
