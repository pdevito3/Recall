import type { BrowserCommand } from 'vitest/node'

import { testHeader, type Answer } from './src/test/answer'
import type { JsonValue } from './src/test/json-value'

// Browser commands for the component tests. They run in Node and drive the Playwright page.
// A test calls them with `commands` from 'vitest/browser'.

type Page = Parameters<BrowserCommand<[]>>[0]['page']
type RouteHandler = Parameters<Page['route']>[1]

interface Hold {
  release: () => void
  matcher: (url: URL) => boolean
  handler: RouteHandler
}

const holds = new Map<string, Hold>()

function matches(path: string): (url: URL) => boolean {
  return (url) => url.pathname + url.search === path
}

/** Holds each request for `path` (pathname and query) until `releaseRequests(path)`. */
export const holdRequests: BrowserCommand<[path: string]> = async ({ page }, path) => {
  if (holds.has(path)) {
    throw new Error(`The requests for ${path} are already held`)
  }
  let release = () => {}
  const held = new Promise<void>((resolve) => (release = resolve))
  const matcher = matches(path)
  const handler: RouteHandler = async (route) => {
    await held
    // Not continue(): Playwright's unroute in releaseRequests can continue an in-flight request first, and a second continue throws "Route is already handled!".
    await route.fallback()
  }
  holds.set(path, { release, matcher, handler })
  await page.route(matcher, handler)
}

/** Lets the held requests for `path` go on, and removes the route. Does nothing if `path` is not held. */
export const releaseRequests: BrowserCommand<[path: string]> = async ({ page }, path) => {
  const hold = holds.get(path)
  if (!hold) return
  holds.delete(path)
  hold.release()
  await page.unroute(hold.matcher, hold.handler)
}

const isServerRequest = (url: URL) => url.pathname.startsWith('/api/') || url.pathname.startsWith('/auth/')

interface SentRequest {
  method: string
  path: string
  body: JsonValue | null
}

interface Answering {
  answers: Answer[]
  unanswered: Set<string>
  sent: SentRequest[]
  test: string
}

const answering = new WeakMap<Page, Answering>()

const isAnswerFor = (request: { method: () => string; url: () => string }) => (answer: Answer) => {
  const url = new URL(request.url())
  return (
    answer.method === request.method() &&
    answer.path === url.pathname &&
    Object.entries(answer.query ?? {}).every(([name, value]) => url.searchParams.get(name) === value)
  )
}

const body = (answer: Answer) => {
  if (answer.contentType !== undefined) return answer.body
  return answer.body === undefined ? '' : JSON.stringify(answer.body)
}

/** A body that is not JSON, such as an upload of audio, counts as no body. */
const jsonBodyOf = (request: Parameters<RouteHandler>[1]): JsonValue | null => {
  try {
    return request.postDataJSON()
  } catch {
    return null
  }
}

export const answerRequests: BrowserCommand<[answers: Answer[]]> = async ({ page }, answers) => {
  const current = answering.get(page)
  if (current) {
    current.answers = answers
    return
  }
  const state: Answering = { answers, unanswered: new Set(), sent: [], test: '' }
  answering.set(page, state)
  await page.route(isServerRequest, async (route) => {
    const request = route.request()
    const test = request.headers()[testHeader]
    if (test !== undefined && test !== state.test) {
      await route.abort()
      return
    }
    if (request.method() !== 'GET') {
      state.sent.push({ method: request.method(), path: new URL(request.url()).pathname, body: jsonBodyOf(request) })
    }
    const answer = state.answers.findLast(isAnswerFor(request))
    if (!answer) {
      const url = new URL(request.url())
      state.unanswered.add(`${request.method()} ${url.pathname}${url.search}`)
      await route.fallback()
      return
    }
    await route.fulfill({
      status: answer.status ?? 200,
      headers: answer.headers,
      contentType: answer.contentType ?? 'application/json',
      body: body(answer),
    })
  })
}

export const beginTest: BrowserCommand<[test: string]> = async (context, test) => {
  await answerRequests(context, [])
  const state = answering.get(context.page)
  if (!state) return
  state.test = test
  state.unanswered.clear()
  state.sent.splice(0)
}

export const takeUnansweredRequests: BrowserCommand<[]> = async ({ page }) => {
  const state = answering.get(page)
  if (!state) return []
  const unanswered = [...state.unanswered]
  state.unanswered.clear()
  return unanswered
}

/** The method, pathname, and JSON body of each request other than a GET since the last call. */
export const takeSentRequests: BrowserCommand<[]> = async ({ page }) => {
  const state = answering.get(page)
  if (!state) return []
  return state.sent.splice(0)
}

/** Sets `prefers-reduced-motion` of the page until the next call. The setup file calls it before each test file. */
export const emulateReducedMotion: BrowserCommand<[reducedMotion: 'reduce' | 'no-preference']> = async ({ page }, reducedMotion) => {
  await page.emulateMedia({ reducedMotion })
}

type Role = Parameters<Page['getByRole']>[0]

/**
 * Moves the mouse onto the element with `role` and `name`, once the element stops moving. `userEvent.hover`
 * scrolls the page first, and a scroll of the page closes an open list.
 */
export const movePointerOnto: BrowserCommand<[role: Role, name: string]> = async ({ iframe, page }, role, name) => {
  const target = iframe.getByRole(role, { name, exact: true })
  await target.waitFor()
  let box = await target.boundingBox()
  for (let tries = 0; tries < 50; tries++) {
    await new Promise((resolve) => setTimeout(resolve, 50))
    const next = await target.boundingBox()
    if (box && next && JSON.stringify(box) === JSON.stringify(next)) {
      await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2)
      return
    }
    box = next
  }
  throw new Error(`The ${role} "${name}" did not stop moving`)
}

/** Presses the left mouse button where the mouse is. */
export const pressPointer: BrowserCommand<[]> = async ({ page }) => {
  await page.mouse.down()
}

/** Releases the left mouse button where the mouse is. */
export const releasePointer: BrowserCommand<[]> = async ({ page }) => {
  await page.mouse.up()
}
