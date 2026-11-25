# Frontend

The frontend is a React 19 SPA in `frontend/`. It uses TanStack Router, TanStack Query, Tailwind v4, and pnpm. The rules are in `docs/frontend/`:

- [server-state.md](frontend/server-state.md): TanStack Query, query keys, mutations, and forms with server data
- [client-state.md](frontend/client-state.md): where to put UI state, effects, and memoization
- [code-style.md](frontend/code-style.md): TypeScript, components, styles, files, and imports
- [testing.md](frontend/testing.md): the test suites, mocks, builders, and selectors

The text of each toast, alert, label, and message follows [messages.md](messages.md).

To add the pages of a feature (a list, a detail page, a create or edit form, or a search picker), follow the [frontend-feature skill](../.agents/skills/frontend-feature/SKILL.md).

## Commands

```bash
cd frontend
pnpm install
pnpm dev      # dev server
pnpm build    # production build
pnpm lint     # Oxlint
pnpm test     # Vitest: unit tests (src/**/*.test.ts, e2e/*.test.ts) in Node, component tests (src/**/*.test.tsx) in headless Chromium
pnpm test:e2e # Playwright: the functional suite (tests/), the SPA against a mocked Server. See docs/frontend/testing.md
pnpm generate:api  # API contract from the OpenAPI document
```

`packageManager` in `frontend/package.json` pins the pnpm version. Current releases of pnpm 10 and later switch to that version automatically. Do not set `pmOnFail` to `ignore` or `warn`, and do not set `manage-package-manager-versions` to false. With these settings, pnpm 11 and later can rewrite `pnpm-lock.yaml`, and the next frozen install then fails.

`engines.pnpm` makes `pnpm install`, `pnpm add`, `pnpm update`, and each script fail on a pnpm older than 10. An older pnpm rewrites `pnpm-lock.yaml`, and the next frozen install then fails with an error such as `ERR_PNPM_LOCKFILE_CONFIG_MISMATCH`. If a command fails with `ERR_PNPM_UNSUPPORTED_ENGINE`, upgrade the global pnpm with the tool that installed it, such as `pnpm i -g pnpm` or `npm i -g pnpm`. Then run `pnpm --version` in `frontend/`. It must print the pinned version. If it prints the old version, an old pnpm comes first in `PATH`.

The component tests use Vitest browser mode with the Playwright provider. Before the first `pnpm test`, install Chromium once with `pnpm exec playwright install chromium`. A component test renders with `render` from `vitest-browser-react`. Find an element in an overlay with `page` from `vitest/browser`, because an overlay renders outside the test container.

## Project structure

```
frontend/src/
├── api/generated*/         # Kubb output. Do not edit
├── domain/                 # One folder for each business area
│   └── [area]/
│       ├── apis/          # API functions, query hooks, and *.keys.ts
│       ├── components/    # Components for this area
│       └── types/         # Types for this area
├── components/
│   ├── ui/                # Shared UI components (shadcn/ui, React Aria, and Animate UI)
│   ├── filter-builder/    # QueryKit filter component
│   ├── app-sidebar.tsx    # Main navigation
│   ├── theme-context.ts   # Theme context and useTheme
│   ├── theme-provider.tsx # Theme provider
│   └── theme-toggle.tsx   # Light, dark, and system toggle
├── routes/                 # TanStack Router file routes
├── hooks/                  # Shared React hooks
├── lib/                    # api-client.ts, pagination.ts, problem-details.ts, utils.ts
└── main.tsx               # App entry point
```

In `components/ui/`, the variants are in `*-variants.ts` files (for example `button-variants.ts`) and the hooks are in `use-*.ts` files. A component from `shadcn add` imports them from the component file, for example `buttonVariants` from `@/components/ui/button`. Change that import to the new file.

Wrap the `cva` call of a `*-variants.ts` file in `withMergedClasses` from `@/lib/utils`. Then a direct call, such as `<Link className={buttonVariants({ variant: "outline" })}>`, gets the classes that the component gets. Without the wrapper, a base class and a variant class for the same CSS property both get to the element, and the order of the CSS rules selects the class that applies.

## API client

`lib/api-client.ts` exports `apiClient`, an Axios instance for the Server:

- `withCredentials: true` sends the session cookie.
- The `X-CSRF: 1` header is necessary for the CSRF protection of the Server.
- An error response that has problem details becomes a `ProblemDetailsError`. The client shows it as a toast by default. To stop the toast, send `{ suppressErrorToast: true }` with the request.

The generated API clients send each request through `apiClient`. `main.tsx` imports `lib/api-client.ts` to connect them before the first request. Use `apiClient` directly only for endpoints that are not in the OpenAPI document, such as `/auth/me`.

A generated client has two error modes. The `throwOnError` option selects the mode:

- `throwOnError: true` (the default): a non-2xx response goes through the `apiClient` error interceptor. The client shows the toast and throws the `ProblemDetailsError`.
- `throwOnError: false`: Kubb sets `validateStatus: () => true`, so axios accepts all statuses. The interceptor does not run. There is no toast and no `ProblemDetailsError`. The client returns the full result. Read `result.status`, and read `result.error` for a non-2xx body.

In both modes, a network error or a cancel still rejects the promise.

```typescript
const result = await getTenant({ path: { id }, throwOnError: false, signal })
if (result.status === 404) {
  // Show the not found state. result.error has the problem details body.
}
```

## API contract

Kubb generates the API contract from the OpenAPI document of the Server. The configuration is in `kubb.config.ts`. The output for v1 is in `src/api/generated/`, and each later version has its own folder, for example `src/api/generated-v2/`. Each folder has one file for each endpoint:

```
src/api/generated/
├── [tag]/
│   ├── clients/[operation].ts  # Axios client function
│   ├── types/[operation].ts    # Request and response types
│   └── zod/[operation].ts      # Zod schemas
├── models/
│   ├── types/[schema].ts       # Shared DTO types
│   └── zod/[schema].ts         # Shared DTO Zod schemas
└── .kubb/                      # Client runtime
```

The tag and the operation names come from the endpoint names (`.WithName`) and the endpoint tags (`.WithTags`). The Server Debug build fails if an endpoint has no name.

To change the contract after a change to the Server API:

1. Build the Server in Debug with `dotnet build Recall.Server --no-incremental`. The build writes `Recall.Server/openapi/Recall.Server.json`. Without `--no-incremental`, a build with nothing to compile does not write the document again.
2. Run `pnpm generate:api` in `frontend/`.
3. Commit the OpenAPI document and the generated files together.

The `api-contract` skill gives the full procedure and the checks.

If a generated file has a merge conflict, do not merge it by hand. Accept either side, then do steps 1 and 2 again. The `api-contract` job of the `CI` workflow fails when the committed files are not current. See [ADR 0001](adr/0001-commit-generated-api-contract.md).

Do not edit the generated files. Oxlint ignores them, but `tsc` compiles them.

- A list client (an operation name that ends in `List`) returns the full response, because the paging metadata is in the `X-Pagination` header. Read the header with `parsePagination` from `lib/pagination.ts`. It returns the `PaginationMetadata` type.
- The other clients return only the response body.
- The Zod schemas are for form validation and tests. The clients do not parse responses with them.

```typescript
import { getTenant } from '@/api/generated/tenants/clients/get-tenant'
import type { TenantDto } from '@/api/generated/models/types/tenant-dto'

const tenant: TenantDto = await getTenant({ path: { id }, signal })
```

```typescript
import { getTenantList } from '@/api/generated/tenants/clients/get-tenant-list'
import { parsePagination } from '@/lib/pagination'

const result = await getTenantList({ query: { pageNumber: 1, pageSize: 10 }, signal })
const tenants = result.data
const pagination = parsePagination(result)
```

### A generated type is wrong

Fix the cause on the Server: the DTO, an attribute such as `[NamesOf<T>]`, or a transformer in `Recall.Server/Resources/Entrypoints/Http/OpenApi/`. Add a test in `OpenApiTransformerTests` or `OpenApiDocumentTests`, then regenerate. The rules of the transformers are in [backend.md](backend.md#openapi-document).

| The generated type | The cause on the Server |
|---|---|
| A property is optional, but the DTO property is not nullable | `RequireNonNullablePropertiesTransformer` |
| A number field also takes a string | `RemoveStringFromNumbersTransformer` |
| A `uint` field has no range | `UnsignedIntegerRangeTransformer` |
| A field of names is a `string`, not a union of the names | `[NamesOf<T>]` on the DTO property, and `FixedNamesTransformer` |
| A request field has no max length, no minimum, or no maximum | `[SchemaMaxLength]`, `[SchemaMinimum]`, or `[SchemaMaximum]` on the request DTO property, and `SchemaLimitsTransformer` |
| A query parameter has no max length | `[MaxLength]` on the parameter of the endpoint handler |
| A request body or a response has a type for each media type | `JsonContentTypeTransformer` |
| A list client has no type for the `X-Pagination` header | `PaginationHeaderTransformer` |
| A secured operation has no 401 response | `UnauthorizedResponseTransformer` |

## Query hooks

Each area keeps its hook and its query key factory in `domain/[area]/apis/`. The hook calls the generated client. Write the hooks and the keys by hand.

```typescript
import { useCurrentUser } from '@/domain/users/apis/get-current-user'
import { useAuth } from '@/domain/auth/apis/get-user'
import { useLogout } from '@/domain/auth/apis/auth-actions'

const { data, isLoading, error } = useCurrentUser()
const { isLoggedIn, username } = useAuth()
const logout = useLogout()
```

Never copy server state into local state. See [server-state.md](frontend/server-state.md).

## Session

Each page needs a session. The root route (`routes/__root.tsx`) reads `authUserQuery` before it shows a page:

| State of the session query | The root route shows |
|---|---|
| Loading | Nothing |
| No session (`null`) | Nothing. The tab already left through `leaveTheApp`, to `/auth/login` with the path, the query, and the hash as `returnUrl`. See [Leaving the app](#leaving-the-app). |
| Error, and no earlier answer | An alert and Try Again. It does not go to the login. While the check runs again, Try Again is pending and takes no press. |
| A user | The layout and the page |

The app does not poll the session. It finds an ended session at the next call to an endpoint that needs a user: the Server answers 401. A call to an endpoint that allows anonymous callers still answers 200. The Server also answers 401 while the auth session lives, for example when a proxy strips the `X-CSRF` header. So on a 401, `apiClient` checks the session again and waits for the answer (`setSessionEndedCheck` in `main.tsx`):

| The check gives | `apiClient` |
|---|---|
| `null` | Shows no toast. The tab leaves for the login. |
| A user | Shows "The Server did not accept your sign-in for this request." unless the call sets `suppressErrorToast`. The page stays. |
| An error | Shows the same toast as for a user. The root route keeps the last answer. |

A 401 of `/auth/me` is a failed check. It does not start a check again.

After a logout, the login goes where the logout went: to the logout of the provider, or to the signed-out page. A call that answers 401 after the logout finds no session, and a login with a return URL would abort that navigation. While the Server signs out, a login waits for its answer. A login does not wait for the question about unsent words, because a session that ended can name no provider.

The session check shows no error toast. The alert is the only message for a failed check. A failed refetch shows no message of its own, because the page still works with the last answer.

Thus a page always has a session. Do not add a Login button or a signed-out state to a page.

### Leaving the app

`leaveTheApp` in `frontend/src/domain/auth/apis/leave-the-app.ts` is the only way out of the signed-in app. Each reason to leave calls it: a sign-out in this tab or in another tab, a user query that reads `null`, a restored page with an ended or a newer session, a restored page with no answer about its session, the session of another person, and `Load the page again` on the error page. Words that only the memory of the tab holds have no key, so the tab shows them first, as the table of [Unsent words](#unsent-words) says. The tab also stops its recording and names the audio of the person that waits in this browser for the Server. The audio stays in this browser and uploads at the next sign-in. A sign-out in this tab does not name again the audio that its question named. An open question about unsent words at a sign-out closes, and that sign-out ends with no error.

The lint rules `recall/no-way-out` and `recall/no-login-page` in `frontend/lint/recall.js` refuse each other way out in `src`: a `reload`, `assign`, or `replace` of `location`, an assignment to `location` or `location.href`, and a path that starts with `/auth/login`. A test can name the login page, but it cannot leave the page of the test. To add a way out, add a reason to `Departure` and call `leaveTheApp`.

### Unsent words

The words of an entry save in the background. Until the Server holds them, they stay in `localStorage` under `recall.unsent.<owner>.<writer>.<number>`. A setting that did not reach the Server stays under `recall.unsent-setting.<owner>.<writer>.<number>`. `<writer>` names one load of a page. The tab keeps a pointer to the open entry in `sessionStorage` under `recall.composer`. The pointer holds no words. Only the person of the key reads or sends these words.

| What happens | What the app does |
|---|---|
| A save gets no answer in 30 s | The request ends, the status says that the save waits, and the save tries again. |
| A save meets an entry that is gone (404, or a 409 that is not a conflict of a save) | The words become a new entry. A notice says so on each page, also on a later load with no page open. The entry page goes to the new entry. |
| The Server refuses the words (another 4xx) | The words stay, with the reason, and they do not go out again on the next load. The page of the words shows the reason. With no such page open, a notice gives the reason, a Copy, and a Discard. |
| A save has a base that is not the current Draft number | The Server answers `Lost`, keeps the words in a Revision, and the Draft stays. The time on the device does not decide. The app reads the Draft before it saves again. If the Draft holds each word of the save, no notice shows. If not, the page keeps the words on the screen and says so. `Add my words to the newest words` joins the words and saves from the read. A later save cannot push a chunk out of the Draft with no read. With no page open, a notice names the words and links to History, and it stays after a reload, until the person closes it or signs out. |
| A retry or another tab sends a save whose first reply did not come | The Server answers `Kept` or `Lost` for a save that it knows. The number in a `Kept` is not a base. The app reads the Draft inside the same send, and it follows the same rule as for a loss. If this read fails, also with a refusal of the Server, the app sends the same save again, and no notice shows until a read answers. A read that finds the entry gone moves the words to a new entry. A later save that is still running decides instead, because the Draft can hold it. |
| A person logs out | A toast says that the app works. Each tab sends its unsent words, and the wait is up to 5 s. If the Server holds them all, the logout goes on. If not, an alert dialog names the words, also those of another tab, each with a Copy. The person can keep them on this device or discard them. A discard removes the exact keys that the dialog named. Words that only the memory of a tab holds cannot stay, so the dialog offers `Stay signed in` in place of `Keep`. The logout then clears the pointer and ends the session. |
| The tab leaves the app: a sign-out in this tab or in another tab, a session that ends with no message (a 401, a restore of a page whose session ended or changed), a restore with no answer about the session, or a new load from the error page | Words that only the memory of the tab holds have no key. The tab shows them alone, each with a Copy, and it goes only when the person selects `Leave this page`. A close of the tab asks first. Audio that waits shows on the same screen, and it stays in this browser. See [Leaving the app](#leaving-the-app). |
| The session belongs to another person (see [One browser, two people](authentication.md#one-browser-two-people)) | The tab stops each saver, clears the query cache and the toasts, keeps the words under the key of the first person, and goes to `/auth/login?anotherPerson=1`. Words that only the memory of the tab holds go at once, because the new person must not read them. Then the page says that they are gone, with `&wordsLost=1`. |

Kept words go out when the same person signs in on this browser again. A refused text cannot go, so it returns as a notice with a Copy and a Discard. The next person sees no sign of them in the app. The browser tools of the device can still read them, and the dialog says so.

#### One owner for each key

Each load of a page has a new writer id. A page writes its text under keys that name that id. A key never changes: a newer text gets a new key, and the page then removes its previous key. So a remove takes only the exact text that the remover read. One saver has at most one key, and that key holds the change in the queue and the held words as one entry. Another tab takes a key only after its writer left, or after its writer did not answer a ping for 500 ms. A writer that leaves writes `recall.left.<writer>` in `pagehide` and sends a message on the `recall.tabs` channel. A wrong guess about a frozen tab sends the same text twice, and the Server takes the second as a replay or as a Revision. No word goes, because safety does not depend on who is alive. `frontend/src/domain/sessions/autosave/tab-keys.ts` holds this rule.

The app runs on plain HTTP on a LAN, so it must not need a secure context. It uses `localStorage` and `BroadcastChannel`, which plain HTTP has. It uses Web Locks only where the browser has them, for the editor of an entry.

#### One tab edits an entry

One tab of a browser edits an entry at a time ([ADR 0022](adr/0022-one-tab-edits-an-entry.md)). `frontend/src/domain/sessions/autosave/entry-lock.ts` holds this rule.

- The tab that edits holds the Web Lock `recall.edit.<person>.<entry>`. A second tab that opens the entry shows the words of the editor with no edit. Its `Edit here` button takes the entry there, and the other tab goes read-only at once.
- The tab that loses hands its unsent words, its change in flight, and its base to the new editor on the `recall.editors` channel. It keeps them until the new editor says that it took them, so a crash of the new editor loses nothing. A frozen tab hands them over when it wakes, and the new editor merges them once from their base.
- A crash or a close frees the lock. The next tab in line takes the words from the key of the editor.
- A load with no page of the entry, such as a load that takes the words of a closed tab, gives the entry to a tab that shows it.
- When the editor leaves a page because another tab took the entry, a focus in the editor goes to `Edit here`. The button names the notice, so a screen reader reads why. The read-only words are a read-only text box.
- On plain HTTP the browser has no Web Locks. A mark `recall.editor.<lock>` in `localStorage` names the editor, and a tab reads it before it writes or sends. A mark of a tab that crashed stays, so the next tab needs `Edit here`.

So no two tabs hold the same unsent words, and a merge after `Lost` always knows its base.

A browser with no `BroadcastChannel`, or one that refuses to make a channel, still opens the app. The words of one tab stay safe, because the keys do not need the channel. With two tabs, no hand-over comes, so a tab that takes an entry starts from the key of the last editor after 500 ms. Words that a full storage left only in the memory of that editor stay there. A tab also does not hear a sign-out or a sign-in in another tab at once. It checks the last person of the browser when it shows again.

A page that opens takes the key of a writer that left. A live tab never copies or takes the key of another live tab. The words of a live editor come only by the hand-over of [One tab edits an entry](#one-tab-edits-an-entry).

#### Limits of the unsent words

**A kill of the browser.** `localStorage` writes reach the memory of the browser at once, and the disk later. Chromium delays the commit to disk. So a kill of the browser process, or a power cut, can lose the last words. A close of a tab or of the browser loses nothing. The table has the runs on Chromium on macOS. Each run typed a word, waited, and killed the process with `SIGKILL`.

| Server state | Wait before the kill | Result |
|---|---|---|
| Saves fail | 0, 500, or 2,000 ms | The word is lost. |
| Saves fail | 6,000 ms or more | The word survives, and it goes out at the next load. |
| Online | 0 or 500 ms | The word is lost, because the 800 ms pause in typing had not ended. |
| Online | After `Saved` shows | The word is on the Server. |

The app cannot change this delay. No web API forces the commit.

**One very long text with no spaces.** Blink and the editor take time that grows faster than the size of one unbroken text. No word goes, and the tab does not answer during that time. The server limit is 1,000,000 characters. A text with spaces of 1.1 M characters takes 244 ms. A change of the wrap rule to `break-word` did not help (200,000 characters: 9.4 s). The notice of a text over the limit can take 36 s to show, and a reload with a dead tab can take 69 s. Real words of the same size take 223 ms.

| Unbroken text | Paste |
|---|---|
| 10,000 characters | 0.1 s |
| 50,000 characters | 1.8 s |
| 100,000 characters | 4.9 s |
| 200,000 characters | 11.2 s |
| 1,100,000 characters | 50 s, and 98 s at a reload |

**Tags.** A change of tags has no key, so a reload or a crash loses a change that did not reach the Server. A sign-out asks first. A session that ends with no message (a 401, or a sign-out in another tab) does not ask, and the screen of words that only the memory holds does not name a tag change. A reload with an open prompt about the same field drops the choice in the prompt, and the row keeps the value that the Server has.

**A reload while the server is down.** No app code runs, so the app cannot show a message. A plain HTTP origin cannot have a service worker, because a service worker needs a secure context. A reverse proxy that stays up shows its own error page. Caddy sends a 502 with an empty body, and the person sees a white page. With nothing listening, the person sees the connection error page of the browser. The words stay in their keys, and they go out when the page opens again after the server returns.

**A sign-out in another tab.** A tab that does not answer within 500 ms cannot report words that only its memory holds, so the sign-out in another tab cannot name them. A frozen tab, or a tab in a long paste, does this. Words that a tab types after the dialog opened stay in the key of that tab, and they return with a notice at the next sign-in. With no room in storage, those later words have no key, so the tab shows them alone with a Copy before it leaves. A discard removes the text that the dialog named, so words that the person discarded return when they share a key with words typed after the list.

**No room in storage.** A key holds the newest words of its saver or nothing. When a write finds no room and the words changed, the older key goes, and the page says that the words are only there. A crash then loses them. A hand-over writes the words to a key again when storage has room.

**One entry open in two composers.** A reload or Duplicate Tab can do this while a second tab is open, because the pointer of a composer stays in `sessionStorage` over a reload. A composer that already reads the entry does not join the held words of a key of another tab. The words stay in the key, they join at a later load, and the tab that joins them says so.

**A tab that wakes.** A frozen tab that wakes after another tab took or discarded its key does not save those words again. If the person types in that tab after the wake, the screen text goes as new text, and the rule of the base decides it. If a key of another tab holds the words, the page of the frozen tab does not say Saved. If no key holds them, the page says that the words may be only on that page, and Done waits for an answer. With no entry yet, "Add my words to the newest words" saves the words as a new entry, and "Show the newest words" empties the page.

**A notice about words in History.** It ends at a sign-out, and it does not return. The words stay in History. With no room in storage, the notice lives only in the memory of the tab, so a crash of the tab ends it.

**Location capture never turns on with no click.** Location capture never turns on from a key, a retry, or a save of another setting. A change to on goes to the Server only from a click in a live page. A save of another setting sends the values of the row at its base, and the Server refuses a save with a stale base, so that save does not turn location on. If the row moved after the click, the click got no reply, the click got a refusal that the app would try again, or the click waits behind a save that failed, location stays off and the page asks. A change to on that waits in a key is dropped when another tab or a later load takes the key. If a tab closes before its click reaches the Server, location stays off, with no notice. A false off is safe. The person can click again.

The app asks at the start of an entry and again at its create. A change to off in the memory of this tab, or in a key of any tab of the person, stops the location, and so does any open question about location on this device. While it waits, each new entry shows a notice that it saves no location. The row of settings stays in memory, so it can be older than a change on another device, and a create can then send coordinates. The Server reads its own row and drops them. A row that has not loaded at the first word also gives no location to that entry. A refused change to off holds only in the tab that made it, the row stays on, and only that tab shows the refusal.

### Recorded audio

The mic card is the first card of the journal home. One tap records into a new entry. The recorder gives a slice each second, and each slice goes to IndexedDB (`recall.capture`) before the upload. A slice leaves the browser store only after the Server answers its `PUT` with a success. `frontend/src/domain/capture` holds this code: `upload-plan.ts` and `capture-view.ts` decide, and `audio-store.ts`, `mic-recorder.ts`, and `upload-queue.ts` do the work.

- The root layout runs the upload queue of the person of the page on each page, so stored audio goes out after a reload or on a later visit. One tab of the browser sends at a time, under the Web Lock `recall.capture.upload.<owner>`. The steps are the Session, the Recording, each slice in order, and the finish with the count and the bytes. Each step is idempotent on the Server, so a retry after a lost answer is safe.
- The entry is made only after the first slice is stored, so a tap with no audio leaves no empty entry.
- A tab that records holds the Web Lock `recall.capture.recording.<id>`. A capture with no holder is one of a closed tab, and the queue ends it and sends it.
- With no network, or with no answer from the Server, the queue waits 1 s, then twice as long each time up to 30 s. The return of the network tries at once. The card says that the audio is safe in this browser.
- A `409` when the app makes the Session, or a `404` when it makes the Recording, means that the person deleted the entry before its audio came. The audio moves to a new entry. A `404` for a slice or the finish means that the person deleted the entry during the upload, so the browser drops its copy.
- Another refusal keeps the audio with the words of the Server, and the card shows `Send again`.
- After the finish, the card follows the Recording: `Writing the words`, then `Your words are in` with the words and `Open entry`, or `No words came from this audio` with `Try again`.

Limits:

- A close of the tab can lose the last second of audio. The recorder asks for the slice in `visibilitychange` and `pagehide`, but the browser does not wait for the write.
- A recording goes on when the person opens another page of the app. Only the card on the journal home shows it.
- Audio that waits at a logout stays under its person, and it goes out at the next sign-in of that person on this browser.
- The browser gives the mic only to a secure context. On plain HTTP the card says that the mic needs HTTPS, and typed entries still work.

## Routes

Each file in `routes/` is a route. The router generates `routeTree.gen.ts`. Do not edit that file.

```typescript
import { createFileRoute } from '@tanstack/react-router'

export const Route = createFileRoute('/users')({
  component: UsersPage,
  staticData: { breadcrumb: 'Users' },
})
```

Each page route sets `staticData.breadcrumb`. The layout header shows it as the name of the page. A test in `src/root-route.test.tsx` fails when a page route has no breadcrumb.

The router moves the `component` of each route to a chunk that loads when the page opens. The other route options, such as `validateSearch`, `loader`, `beforeLoad`, and `staticData`, stay in the entry chunk with each module that they import. These rules keep the entry chunk small:

- Check the search params with a zod object. Take each param schema from the contract, and add `.catch(undefined)`, so that a value that the contract refuses falls back to the default:

  ```typescript
  const listSearchSchema = z.object({
    sortOrder: getUserListQuerySortOrderSchema.catch(undefined),
    page: getUserListQueryPageNumberSchema.catch(undefined),
  })

  export const Route = createFileRoute('/users')({
    validateSearch: listSearchSchema,
    component: UsersPage,
    staticData: { breadcrumb: 'Users' },
  })
  ```

  A param that the contract does not have, such as the search text of a list that the hook turns into a filter, gets its own schema with the max length of the field that it matches: `z.string().max(searchMaxLength).optional().catch(undefined)`.

  zod has its own vendor chunk (`vendorChunks` in `frontend/vite.config.ts`), so it is not in the entry chunk. When a route checks its search with zod, the zod chunk loads on each first page. It is 67 kB, 18 kB with gzip.
- Import a type with a top-level `import type`. Do not write `import { Thing, type Option }`. The router keeps the import of a type that the component uses as `import { type Option }`, and TypeScript keeps that line as an import of the module. So the module and each module that it imports load with the entry chunk. `pnpm lint` fails on an inline type import in `routes/`.
- Do not import a component or a large library in `validateSearch`, `loader`, or `beforeLoad`.

## Bundle size

`pnpm build` fails when the JavaScript that `dist/index.html` loads is more than 965 kB of minified code. That is the entry chunk in the `<script type="module">` tag and each chunk in the `modulepreload` links, which the entry imports statically. The budget does not count the CSS, or the chunk of a route. `main.tsx` loads the chunk of each route at the first idle moment after the first page shows, because a page with no network cannot fetch a chunk, and the status line tells a person to keep the page open. The budget is `firstPageBudgetKb` in `frontend/vite.config.ts`. The error gives the size of each chunk that it counts. A build that passes logs the size of the first page.

`motion` (151 kB) does not load on the first page. Each animated overlay of the layout, such as the tooltip of the sidebar, the user menu, or the dialog of unsent words at logout, is closed on the first paint. So the layout loads each one with `lazyOverlay` from `components/ui/lazy-overlay.tsx`, and imports its trigger from a module with no `motion`, such as `components/ui/tooltip-trigger.tsx`. A static import of `tooltip.tsx`, `popover.tsx`, `dropdown-menu.tsx`, or another component of `@animate-ui/react` in the layout loads `motion` on the first page again.

When the build fails:

1. List the modules of each chunk:

   ```bash
   cd frontend
   pnpm dlx vite-bundle-visualizer -t list -o dist-stats.yml --open false
   ```

   The command fails with the budget error, but it writes the list first. The list can contain NUL bytes, so search it with `grep -a` or open it in an editor.
2. In `dist-stats.yml`, read the modules under the chunks that the budget error names. Find the module that should not be there.
3. Find the import that brings it in. Usually it is a route option or an inline type import in a route file. See the rules in [Routes](#routes).
4. If a vendor library must load on the first page, you can add it to `vendorChunks` in `frontend/vite.config.ts`. It then gets its own chunk, which the browser caches apart from the app code. The chunk still loads on the first page, so the budget still counts it. A move to `vendorChunks` does not make a build pass.
5. Increase the budget only when the first page must have the new code. Write the reason in the commit message.

## Links

To link to a route, use `Link` from `@/components/ui/link`. Give the path of the route in `to`, and give its params in `params`. TypeScript checks both against the route tree:

```typescript
import { Link } from '@/components/ui/link'

<Link to="/users/$id" params={{ id: user.id }}>{user.fullName}</Link>
```

- Do not build the path as a string, such as `` `/users/${user.id}` ``. TypeScript does not check a string path.
- `search`, `hash`, `replace`, and the other options of the TanStack Router `Link` also work.
- The link is a React Aria `Link`: use `onPress`, not `onClick`. A click with Cmd, Ctrl, or Shift, and a middle-click, open the link in a new tab or window.
- The link sets `aria-current="page"` only when it goes to the current page, with the same search params.
- To disable a link, set `disabled`.
- `BreadcrumbLink`, `SidebarMenuLink`, and `SidebarMenuSubLink` take the same props.
- `SidebarMenuLink` and `SidebarMenuSubLink` set `aria-current` from their `current` prop, not from the router: `page` on the page of the item, and `true` on a page under it. The `useCurrentNavItem` hook in `components/current-nav-item.ts` picks the one current item of the sidebar with the match of the router.
- To make another link component, render `RouterLinkPrimitive` in it and wrap it with `createRouterLink`. See `BreadcrumbLink` in `components/ui/breadcrumb.tsx`.

The lint does not let you import `Link` from `react-aria-components`, `@animate-ui/react/primitives/react-aria`, or `@tanstack/react-router`.

Other React Aria components take a plain `href`, for example `MenuItem` and `Tab`. The root route wraps the app in `HrefRouterProvider` from `@/components/ui/link`, so the router goes to the `href` with no page load. TypeScript does not check a plain `href`, so for a link to a route, prefer a typed wrapper made with `createRouterLink`. An `href` that has no route in the SPA, such as `/auth/login`, loads the page from the server.

## Theme

`useTheme()` from `@/components/theme-context` returns `theme` and `setTheme`. The values are `'light'`, `'dark'`, and `'system'`. The theme persists in localStorage.

## Filter builder

`FilterBuilder` makes filter strings for QueryKit list endpoints.

```typescript
import { FilterBuilder } from '@/components/filter-builder/filter-builder'
import { toQueryKitString } from '@/components/filter-builder/utils/querykit-converter'

const filterConfig = [
  { propertyKey: 'firstName', propertyLabel: 'First Name', controlType: 'text' },
  { propertyKey: 'status', propertyLabel: 'Status', controlType: 'multiselect',
    options: [{ value: 'active', label: 'Active' }] },
  { propertyKey: 'createdAt', propertyLabel: 'Created', controlType: 'date' },
]

<FilterBuilder
  filterOptions={filterConfig}
  onChange={(state) => {
    const queryString = toQueryKitString(state)
    // 'firstName == "John" && status ^^ ["active"]'
  }}
/>
```

The control types are text, number, date, boolean, and multiselect. Groups can use AND or OR. For a date of a place, give the config the `timeZone` of the place. See "Dates and times" in [code-style.md](frontend/code-style.md).

## Forms

Use React Hook Form with a Zod schema:

```typescript
const schema = z.object({
  name: z.string().min(1, 'Required'),
  email: z.string().email(),
})

const form = useForm({
  resolver: zodResolver(schema),
  defaultValues: { name: '', email: '' },
})
```

Take each limit of a form field from the generated Zod schema of its DTO, not from a copy of the number. `maxLengthOf`, `minimumOf`, and `maximumOf` in `lib/api-contract.ts` read the limit. For a text field, use `stringWithMaxLengthOf`, not `z.string().max()`. The Server counts code points, but `.max()` counts UTF-16 units and refuses text that the Server takes. These functions throw when the contract has no limit. To fix that, add `[SchemaMaxLength]`, `[SchemaMinimum]`, or `[SchemaMaximum]` to the DTO on the Server, then regenerate the contract (see [API contract](#api-contract)). A `uint` field always has a minimum and a maximum in the contract, so `minimumOf` and `maximumOf` do not catch a missing `[SchemaMinimum]` or `[SchemaMaximum]` on a `uint`.

```typescript
const firstName = userForCreationDtoSchema.shape.firstName
const schema = z.object({ firstName: stringWithMaxLengthOf(firstName).min(1, 'Required') })
```
