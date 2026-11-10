export type HttpMethod = 'get' | 'post' | 'put' | 'patch' | 'delete'

export interface MockRoute {
  method: HttpMethod
  /** A pattern answers a read of each id, such as the Recordings of any entry. */
  path: string | RegExp
  status: number
  body?: unknown
  headers?: Record<string, string>
  /** Holds the answer, so that a later request can overtake this one. */
  delayMs?: number
  /** Holds the answer until the test settles this promise, so that the call ends at the step the test chooses, however slow the page is. */
  heldUntil?: Promise<unknown>
  /** The call stays open until the page leaves, as a read that hangs does. */
  neverAnswers?: boolean
  /** Answers one call. The next call gets the older mock of the path, so a test sets each answer of a sequence before the page asks. */
  once?: boolean
}

// The SPA sends these paths to the Server. The Vite dev server serves the source files of the app under /src,
// so a source file such as /src/api/generated/... does not match.
const mockedPrefixes = ['/api/', '/auth/']

export const isMockedPath = (pathname: string) => mockedPrefixes.some((prefix) => pathname.startsWith(prefix))

export const answers = (route: MockRoute, method: string, pathname: string) =>
  route.method === method && (typeof route.path === 'string' ? route.path === pathname : route.path.test(pathname))

export interface ServedRequest {
  query: URLSearchParams
  headers: Headers
  body: unknown
}

// A mock matches on method and path alone. Only a test that reads the request back can prove
// what the application put on the wire.
export function requestRecorder() {
  const served: { method: HttpMethod; url: URL; headers: Headers; body: unknown }[] = []

  const all = (method: HttpMethod, path: string): ServedRequest[] =>
    served
      .filter((r) => r.method === method && r.url.pathname === path)
      .map((r) => ({ query: r.url.searchParams, headers: r.headers, body: r.body }))

  return {
    add(method: HttpMethod, url: URL, headers: Headers, body: unknown) {
      served.push({ method, url, headers, body })
    },
    all,
    last: (method: HttpMethod, path: string) => all(method, path).at(-1),
  }
}
