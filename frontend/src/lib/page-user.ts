export const pageUserHeader = 'X-Recall-User'

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

export function notePageUser(sub: string) {
  pageUser ??= sub
  if (pageUser !== sub) refuseThePage()
}
