import { openChannel } from './browser-channel'

export const pageUserHeader = 'X-Recall-User'

export const sessionChannelName = 'recall.session'

const lastPersonKey = 'recall.lastPerson'

let pageUser: string | null = null
let leaveThePage = () => undefined as void

/** All tabs of a browser share one cookie, so the Server needs each request to say whose page sends it. See docs/authentication.md. */
export const pageUserOf = () => pageUser

export function setPageUserMismatchHandler(handler: () => void) {
  leaveThePage = handler
}

export function refuseThePage() {
  leaveThePage()
}

/** A message evicts a page from the back-forward cache, so a next page of the same person sends none. */
function announceAChangeOfPerson(sub: string) {
  try {
    if (localStorage.getItem(lastPersonKey) === sub) return
    // A refused write must not leave an earlier person here, or the return of that person sends no message.
    localStorage.removeItem(lastPersonKey)
    localStorage.setItem(lastPersonKey, sub)
  } catch {
    // With no room to store the person, the message still goes.
  }
  const otherTabs = openChannel(sessionChannelName)
  otherTabs?.postMessage({ signedInAs: sub })
  otherTabs?.close()
}

export function notePageUser(sub: string) {
  if (pageUser === null) announceAChangeOfPerson(sub)
  pageUser ??= sub
  if (pageUser !== sub) refuseThePage()
}

/** A frozen tab can miss the message of a sign-in, and the key of the last person still tells when the person sees the tab again. */
export function checkLastPersonOfThisBrowser() {
  let lastPerson: string | null = null
  try {
    lastPerson = localStorage.getItem(lastPersonKey)
  } catch {
    // With no storage, the message and the header of each request still guard the page.
  }
  if (lastPerson !== null) noteSignInOfAnotherTab(lastPerson)
}

/** A tab of another person must not keep showing its words after a second person signs in on this browser. */
export function noteSignInOfAnotherTab(sub: string) {
  if (pageUser !== null && pageUser !== sub) refuseThePage()
}
