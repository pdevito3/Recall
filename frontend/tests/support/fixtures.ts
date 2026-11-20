import { test as base, expect, type Request, type Route } from '@playwright/test'
import { authApi } from './api/auth-api'
import { membershipApi } from './api/membership-api'
import { modelProvidersApi } from './api/model-providers-api'
import { peopleApi } from './api/people-api'
import { sessionsApi } from './api/sessions-api'
import { fileStoreApi } from './api/file-store-api'
import { recordingsApi } from './api/recordings-api'
import { aSetting, settingsApi } from './api/settings-api'
import { transcriptionEngineApi } from './api/summaries-api'
import { usersApi } from './api/users-api'
import { answers, mockedPaths, requestRecorder, type HttpMethod, type MockRoute, type ServedRequest } from './api/mock-route'
import { as, type Identity } from './auth/actors'

export interface Api {
  /** Serve these canned responses for the rest of the test. A later route wins over an earlier one. */
  mock(...routes: MockRoute[]): void
  /** The last request that the seam served for this method and path, or undefined if there was none. */
  last(method: HttpMethod, path: string): ServedRequest | undefined
  /** Each request that the seam served for this method and path, in order. */
  all(method: HttpMethod, path: string): ServedRequest[]
}

// `postDataJSON` throws on a body that is not JSON. That leaves the route unfulfilled and the test
// times out, so a recorder must not let it throw.
function postBody(request: Request): unknown {
  try {
    return request.postDataJSON()
  } catch {
    return request.postData() ?? undefined
  }
}

export const test = base.extend<{ actor: Identity; api: Api }>({
  actor: [as.user(), { option: true }],

  // `provide`, not `use`: a bare `use(...)` reads as a React hook to the lint rule.
  api: [
    async ({ page, context, actor, baseURL }, provide) => {
      const origin = new URL(baseURL!).origin
      const routes: MockRoute[] = [
        authApi.userIs(actor),
        ...membershipApi.userIs(actor),
        ...usersApi.currentOf(actor),
        settingsApi.are(aSetting()),
        fileStoreApi.takesWrites(),
        recordingsApi.jobsRun(),
        recordingsApi.noneForAnyEntry(),
        sessionsApi.timelineIs([]),
        peopleApi.listIs([]),
        modelProvidersApi.activeIs(null),
        transcriptionEngineApi.isLocal(),
      ]
      const served = requestRecorder()
      const missed: string[] = []
      const openCalls: Route[] = []
      // A test that fails before it settles a `heldUntil` would leave its call open, and the wait of `unrouteAll` would never end.
      // A `delayMs` that runs past the end of the test would hold the teardown for the rest of the delay.
      let endHeldCalls!: () => void
      const testEnded = new Promise<void>((resolve) => {
        endHeldCalls = resolve
      })

      // On the context, so that a second tab of the test gets the same answers.
      await context.route(
        mockedPaths(origin),
        async (route, request) => {
          const url = new URL(request.url())
          const method = request.method().toLowerCase()
          const newestMock = routes.findLast((r) => answers(r, method, url.pathname))

          // Playwright fails a test on a failed assertion, never on an HTTP status. So the 501 alone
          // proves nothing, and the miss is asserted at teardown.
          if (!newestMock) {
            missed.push(`${method.toUpperCase()} ${url.pathname}`)
            await route.fulfill({ status: 501, body: `No mock for ${method} ${url.pathname}` })
            return
          }

          if (newestMock.once) routes.splice(routes.indexOf(newestMock), 1)
          served.add(newestMock.method, url, new Headers(await request.allHeaders()), postBody(request))
          if (newestMock.neverAnswers) {
            openCalls.push(route)
            return
          }
          if (newestMock.delayMs) await Promise.race([new Promise((resolve) => setTimeout(resolve, newestMock.delayMs)), testEnded])
          if (newestMock.heldUntil) await Promise.race([newestMock.heldUntil, testEnded])
          await route.fulfill({
            status: newestMock.status,
            headers: newestMock.headers,
            ...(newestMock.body === undefined
              ? {}
              : { contentType: 'application/json', body: JSON.stringify(newestMock.body) }),
          })
        },
      )

      await provide({
        mock: (...added) => routes.push(...added),
        last: served.last,
        all: served.all,
      })

      endHeldCalls()
      await Promise.all(openCalls.map((route) => route.abort().catch(() => undefined)))
      // Let an in-flight handler finish, so that a late call is still recorded.
      await page.unrouteAll({ behavior: 'wait' })
      await context.unrouteAll({ behavior: 'wait' })
      expect(missed, 'the page called an API route that no test mocked').toEqual([])
    },
    { auto: true },
  ],
})

export { expect }
