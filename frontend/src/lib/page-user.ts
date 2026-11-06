export const pageUserHeader = 'X-Recall-User'

export const sessionChannelName = 'recall.session'

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

function announceThePerson(sub: string) {
  const otherTabs = new BroadcastChannel(sessionChannelName)
  otherTabs.postMessage({ signedInAs: sub })
  otherTabs.close()
}

export function notePageUser(sub: string) {
  if (pageUser === null) announceThePerson(sub)
  pageUser ??= sub
  if (pageUser !== sub) refuseThePage()
}

/** A tab of another person must not keep showing its words after a second person signs in on this browser. */
export function noteSignInOfAnotherTab(sub: string) {
  if (pageUser !== null && pageUser !== sub) refuseThePage()
}
