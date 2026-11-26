// Setup for the component tests in browser mode (*.test.tsx).
// The app styles load, so that a test sees the real layout and the real visibility of each element.
import '@/index.css'

import { afterEach, beforeAll, beforeEach } from 'vitest'
import { commands } from 'vitest/browser'
import { cleanup } from 'vitest-browser-react'

import { testHeader, type Answer } from './answer'

// The commands are in vitest.commands.ts. They run in Node and drive the Playwright page.
declare module 'vitest/browser' {
  interface BrowserCommands {
    /** Answers each request that matches an answer. A later answer wins. `[]` removes all answers. */
    answerRequests: (answers: Answer[]) => Promise<void>
    /** Releases each held request, removes all answers, and forgets the requests so far. A request of an earlier test does not count after this. */
    beginTest: (test: string) => Promise<void>
    takeUnansweredRequests: () => Promise<string[]>
    /** The method, pathname, and JSON body of each request other than a GET since the last call. */
    takeSentRequests: () => Promise<{ method: string; path: string; body: unknown }[]>
    /** Holds each request for `path` (pathname and query) until `releaseRequests(path)`. */
    holdRequests: (path: string) => Promise<void>
    /** The count of the requests for `path` that wait on `holdRequests(path)` now. */
    countHeldRequests: (path: string) => Promise<number>
    /** Lets the held requests for `path` go on. */
    releaseRequests: (path: string) => Promise<void>
    /** Moves the mouse onto the element with `role` and `name` without a scroll of the page. */
    movePointerOnto: (role: string, name: string) => Promise<void>
    /** Presses the left mouse button where the mouse is. */
    pressPointer: () => Promise<void>
    /** Releases the left mouse button where the mouse is. */
    releasePointer: () => Promise<void>
    /** Sets `prefers-reduced-motion` of the page. */
    emulateReducedMotion: (reducedMotion: 'reduce' | 'no-preference') => Promise<void>
  }
}

// vitest-browser-react unmounts the page of the last test only before the next test, so a request of the last test
// can reach the route late.
let test = 0
const send = XMLHttpRequest.prototype.send
XMLHttpRequest.prototype.send = function (body) {
  this.setRequestHeader(testHeader, String(test))
  send.call(this, body)
}

// Without an animation, a sheet opens in its place. A click on a sheet that slides in can press its button and release
// on the button below it, and then the click does nothing. Each file starts so, because the test files that run in one
// page share the setting.
beforeAll(() => commands.emulateReducedMotion('reduce'))

beforeEach(async () => {
  await cleanup()
  test += 1
  await commands.beginTest(String(test))
})

// A request with no answer gets an error from the dev server. A test can still pass by mistake, for example when
// the page shows the same text for an error and for an empty answer.
afterEach(async () => {
  const unanswered = await commands.takeUnansweredRequests()
  if (unanswered.length > 0) {
    throw new Error(`No answer for these requests: ${unanswered.join(', ')}. Answer them with commands.answerRequests.`)
  }
})
