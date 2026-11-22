# Authentication

Recall owns its identity ([ADR 0014](adr/0014-app-owned-identity.md)). The Server is the OpenID Connect client of each provider. A provider proves who a person is one time. Then the Server issues its own auth session, and the rest of the app reads only that session. The browser holds an opaque cookie, and a native app holds an opaque bearer token. Neither one holds a provider token.

One process, `Recall.Server`, serves the SPA, the API, and the login. The session stays in an `HttpOnly` cookie, so no token reaches the scripts of a page.

The Server code is in `Recall.Server/Auth/` and `Recall.Server/Resources/Entrypoints/Auth/`.

## Endpoints

These routes are not in the OpenAPI document. This section is their contract.

| Path | Method | Credential | Content |
|---|---|---|---|
| `/auth/status` | GET | none | The instance, its state, the callback URL, and each login method |
| `/auth/login` | GET | none | The login page. A live session redirects to the return path. With one ready provider and no `signedOut`, it redirects to the start of that provider. With each provider down, it answers 503 with `Try again`. |
| `/auth/oidc/{id}/start` | GET | none | Starts a login. The query has `client=web` or `client=app`. With `prompt=login`, the provider must ask who the person is, unless its discovery document lists `prompt_values_supported` without `login`. |
| `/auth/oidc/{id}/link` | POST | web session, `X-CSRF: 1` | Starts a link of the provider to the person of the session. It answers JSON with the `url` of the provider. See [Link a provider](#link-a-provider). |
| `/auth/oidc/callback` | GET | none | The return from the provider |
| `/auth/oidc/confirm` | POST | none | The form of the native confirm page |
| `/auth/token` | POST | none | Gives the tokens of a native session |
| `/auth/logout` | POST | optional | Ends the session that the request names. See [Logout](#logout) for each answer. A cookie with no `X-CSRF: 1` gets 401, and nothing ends. |
| `/auth/sessions/revoke-others` | POST | web or native session | Ends each other session and each personal access token of the user. It answers 204. A personal access token gets 403. |
| `/auth/me` | GET | optional | The user of the session. See [The user of a session](#the-user-of-a-session). |
| `/auth/password/login` | POST | none | The password form of the login page. See [Password login](#password-login). |
| `/auth/password/setup` | GET, POST | the setup code | The first setup of an install with no provider and no tenant. With a provider, it answers 404. |
| `/auth/password/choose` | POST | a choose attempt | The choice of a password after a temporary password |
| `/auth/password/change` | POST | web or native session | The change of a password. See [Change a password](#change-a-password). A personal access token gets 403. |
| `/auth/password/claim-owner` | GET, POST | a fresh `claim-owner` file | The new password of the Owner. See [Recover a lost Owner](#recover-a-lost-owner). |

Each other Server path, also `/api/*`, reads the credential in the same way. Send the cookie with `X-CSRF: 1`, or send `Authorization: Bearer <token>`. The one exception is the job dashboard at `/backwave`. See [CSRF](#csrf).

## Web login

1. The SPA calls `/auth/me` before it shows a page. If the answer is 204, it goes to `/auth/login`, with the path, the query, and the hash of the page as `returnUrl`. Each page needs a session.
2. `/auth/login` redirects to `/auth/oidc/{id}/start?client=web`. The return path must be local. Each other value becomes `/`.
3. The start adds an attempt row and sets a binding cookie. It redirects to the provider with `state`, `nonce`, and a PKCE challenge (`S256`). The scope is `openid profile email`.
4. The provider redirects to `/auth/oidc/callback`. The Server finds the attempt by `state`, checks the binding cookie, and consumes the attempt. An attempt can pass this check one time.
5. The Server checks `iss` (RFC 9207), redeems the code, and validates the ID token. It reads the user info, and a user info `sub` that differs from the ID token fails the login. An error body of a provider never goes to a log.
6. The Server finds or adds the user. See [First login and the Owner](#first-login-and-the-owner).
7. The Server adds a session row, sets the session cookie, and redirects to the return path.
8. The first HTML load has no session cookie, because the navigation comes from another site. The SPA calls `/auth/me` from the same site, with the cookie, and shows the page.

## Native login

An app holds one setting, the base URL of the Server. It holds no provider setting and no client secret.

### Sign in

1. Call `GET /auth/status`:

   ```json
   {
     "instance": "recall",
     "state": "ready",
     "callbackUrl": "https://recall.example.com/auth/oidc/callback",
     "message": null,
     "setupRequired": false,
     "modes": [
       {
         "kind": "oidc",
         "id": "keycloak",
         "displayName": "Keycloak",
         "state": "ready",
         "detail": null,
         "startUrl": "/auth/oidc/keycloak/start?client=app"
       }
     ]
   }
   ```

   `state` is `ready`, or `degraded` when each mode is down. The state of a mode is `ready` or `unreachable`. A mode of kind `password` has the `startUrl` `/auth/login?client=app`, and the person types the password on that page. See [Password login](#password-login).
2. Open `startUrl` in the system browser. Add these query values:

   | Name | Value |
   |---|---|
   | `redirect_uri` | `recall://auth/callback`, or a loopback URL with a port, for example `http://127.0.0.1:{port}/callback` or `http://[::1]:{port}/callback` |
   | `code_challenge` | The base64url SHA-256 of a verifier, 43 characters |
   | `code_challenge_method` | `S256` |
   | `state` | A value of the app |

3. The person signs in at the provider. Then the Server shows the page `Sign in to the Recall app?`, also when the provider signs the person in with no click (RFC 8252 section 8.6). The code reaches the app only after the person chooses `Continue`.
4. The Server redirects to `redirect_uri` with `code` and `state`. A failed login redirects with `error` and `state`. The error is `signup_closed`, `access_denied`, `idp_unreachable`, or `invalid_response`.
5. Compare `state`, then send the code in a form within 2 minutes:

   ```http
   POST /auth/token
   Content-Type: application/x-www-form-urlencoded

   grant_type=authorization_code&code=...&code_verifier=...&redirect_uri=...
   ```

   The answer has `Cache-Control: no-store`:

   ```json
   { "access_token": "rcl_at_...", "refresh_token": "rcl_rt_...", "token_type": "Bearer", "expires_in": 900 }
   ```

A code works one time. If the answer to this request is lost, the app signs in again. The session that the lost answer named stays until it expires, or until `revoke-others` ends it.

### Refresh

The access token lives 900 seconds. The refresh token lives 90 days from its last use, with no absolute limit, because a phone that captures a thought after a long gap must not need a sign-in at that moment. Each refresh gives a new pair, and the old access token stops at once.

```http
POST /auth/token
Content-Type: application/x-www-form-urlencoded

grant_type=refresh_token&refresh_token=...&retry_key=...
```

An app must obey these rules:

- **Refresh one time for each token.** Refresh when the access token expires, or on a 401 whose failed token is still the stored token. On a 401 for an older token, send the request again with the stored token. In one process, let one call refresh and let the other calls wait for its result.
- **Make one `retry_key` for each refresh token.** The key has at least 43 base64url characters, for example 32 random bytes. Make it once, and store it with the refresh token before the first send. Keep both in storage that each process of the app shares.
- **Repeat on a lost answer.** When the network fails, send the same refresh token and the same key again. The Server computes the same pair from these two values and writes nothing, so each copy gets the same answer, in any order. A second process that sends the same two values gets the same pair too.
- **Store the new pair before you use it.** Then make a new key for the new refresh token.

An older refresh token, sent with no key or with another key, is a replay. The Server ends the session and answers `invalid_grant`. So a thief who uses a stolen token first ends the session at the next refresh of the real app. One limit stays. A delayed copy of a request that lands after the next rotation also ends the session. It needs a delay of about 15 minutes.

| Answer | Meaning | What the app does |
|---|---|---|
| 200 | The new pair | Stores it |
| 400 `invalid_request` | A missing grant or refresh token, a `retry_key` that is too short, or two `retry_key` values | Corrects the request. The Server wrote nothing. |
| 400 `invalid_grant` | The token is not valid, or the session ended | Shows the sign-in, and keeps each draft on disk |

### Sign out

`POST /auth/logout` with `Authorization: Bearer <access token>`, with the form value `refresh_token`, or with both. It answers 204 also for a token that is not valid, so an app with an expired access token can still end its session. Logout finds a native session by the family inside the refresh token and does not check the secret. The family exists only inside the token, and the tolerant answer is a deliberate decision. `POST /auth/sessions/revoke-others` ends each other session of the user, for example on a lost phone.

Over plain http, a person on the same network can read the tokens and the key. The same person can already read the journal on that connection, so Recall allows it. Use TLS to close both.

## Sessions

One row of `auth_sessions` is one signed-in device or one [personal access token](#personal-access-tokens). The row holds only hashes. A database reader gets no usable credential.

| Credential | Lifetime |
|---|---|
| Web cookie | 30 days with no use, 180 days from the login. Only `/auth/me` extends it, at most once a day. So an API call never writes. |
| Native access token | 900 seconds |
| Native refresh token | 90 days from its last use |
| Login attempt | 10 minutes |
| App code | 2 minutes |
| Personal access token | The expiry that the person picked, or until a revoke |

A session makes no call to the provider. So a web or native session keeps working when the provider is down, and it keeps working when the provider disables the person. To end the access of a person, end the session with `revoke-others` or delete the user. Recall has no back-channel logout.

### Cookies

| Cookie | Attributes | Content |
|---|---|---|
| `__Host-recall`, on TLS | `HttpOnly`, `Secure`, `SameSite=Strict`, `Path=/` | An opaque session value |
| `recall`, on http | `HttpOnly`, `SameSite=Strict`, `Path=/` | The same |
| `__Host-recall_login_{8 hex}`, on TLS | `HttpOnly`, `Secure`, `SameSite=Lax`, `Path=/`, 10 minutes | The binding of one login attempt |
| `recall_login_{8 hex}`, on http | `HttpOnly`, `SameSite=Lax`, `Path=/auth`, 10 minutes | The same |
| `__Host-recall_signed_out`, on TLS | `HttpOnly`, `Secure`, `SameSite=Lax`, `Path=/`, 30 days until the provider returns, then 60 seconds. 60 seconds with a provider that has no `end_session_endpoint` | The mark of a sign-out that this browser finished |
| `recall_signed_out`, on http | `HttpOnly`, `SameSite=Lax`, `Path=/`, 30 days until the provider returns, then 60 seconds. 60 seconds with a provider that has no `end_session_endpoint` | The same |

The binding cookie is `Lax` because the provider redirects with a cross-site GET. On https, the Server reads only `__Host-recall`, so a `recall` cookie from a plain http neighbor has no effect.

### CSRF

A cookie with no `X-CSRF: 1` header fails with 401 on each method, also at `/auth/logout`. The one exception is the job dashboard at `/backwave`, because a browser that opens a page cannot send the header. The antiforgery token of each BackWave write guards that path. A refused GET from another site reloads once with the `SameSite=Strict` cookie, so a link from another site can open a dashboard page of the Owner. BackWave does no write on a GET. Check this again at each upgrade of BackWave. A cross-site page cannot set that header without a CORS grant, and the Server gives none. A bearer token needs no header.

### One browser, two people

All tabs of a browser share one session cookie. A tab that a person left open sends the cookie of the next person who signs in on that browser. So each request of the SPA to `/api/*` names the user that its page belongs to, in the `X-Recall-User` header. The value is the `sub` of the first `GET /auth/me` of the page.

| Request | Answer |
|---------|--------|
| A cookie, a write, and the `sub` of the session | The request runs. |
| A cookie, a write, and any other value or no header | 409 problem with the type `urn:recall:problem:page-user-mismatch`. Nothing changes. |
| A cookie, a read, and another `sub` | The same 409. |
| A cookie and a read with no header | The request runs. |
| A bearer token | The Server does not read the header. |

The check is in the Server, because a check in the SPA alone leaves a race: the cookie can change while a save is on its way. When the SPA gets this refusal, or when `GET /auth/me` names another person, it stops each save, clears the private data from the page, keeps the unsent words under the key of the first person, and goes to `/auth/login?anotherPerson=1`. The tab does not wait and does not ask, because the private text must leave the screen at once. So when the browser had no room to keep some words, those words go at that moment, and the sign-in page says that they are gone. The page told the person about this risk when the write failed, and it offered `Copy words`. That page shows the sign-in choices and a message, and it does not go to the journal of the person who is signed in. See `docs/frontend.md`.

A cookie value works only as a cookie and only on a web session. A bearer value works only in the header and only on a native session. So a stolen cookie value cannot skip this rule.

## Logout

The SPA sends `POST /auth/logout` with `X-CSRF: 1`. The Server ends the session, deletes the cookie, and sets the signed-out mark. If the provider of the session has an `end_session_endpoint`, the answer names its logout URL, and the SPA goes there. The provider ends its session and returns to `/auth/signed-out`, which goes to `/auth/login?signedOut=1`. If the provider has no such endpoint, or does not answer, the SPA goes to `/auth/login?signedOut=1` at once. If the request fails, the SPA stays on the page and says that the person is still signed in.

A response tells what happened:

| Request | Answer |
|---|---|
| A cookie with `X-CSRF: 1`, and the provider of its session has an `end_session_endpoint` | Ends the session if it is live, deletes the cookie, and sets the mark. 200 with `{ "provider_logout_url": "..." }`. |
| A cookie with `X-CSRF: 1`, in each other case | The same, and 204 with no body. |
| `X-CSRF: 1` and no token (a value that is not a refresh token counts as no token), with no cookie or with a cookie of a session that Recall cannot find, on a Server with one provider | This browser can still have a session at the provider, so the Server treats that provider as the provider of the session. The first row or the second row then applies. |
| The same, on a Server with no provider or with more than one | Sets the mark for 60 seconds, and 204 with no body. Recall does not know the provider, so the page has no note. The page with more than one provider does not start a login with no click. |
| A cookie with no `X-CSRF: 1`, with or without another credential | 401. Nothing ends, and the cookie stays. |
| No cookie, and no `X-CSRF: 1` or a bearer token or a refresh token | Ends what a bearer token or a refresh token names. 204, with no mark. A native app opens no browser, so its logout does not touch the provider. A cross-site page gets this too, because `SameSite=Strict` holds the cookie back. |
| `GET /auth/logout` | 405. A link cannot end a session. |

`/auth/login?signedOut=1` starts nothing with no click. It says `You are signed out` only with the mark, so a link from another site cannot make it say that. A live session at this URL goes to the return path.

The mark is `SameSite=Lax`, because a provider on another site returns with a cross-site GET. It lives 30 days until the provider returns, so that a person who closes the confirm page of a provider does not sign in again with no click on a later visit. After the return, it lives 60 seconds. A provider with no `end_session_endpoint` cannot return, so its mark also lives 60 seconds. A new login deletes it, so a link cannot show the signed-out page next to a live session. The session cookie stays `Strict`.

### Other tabs, Back, and other browsers

A logout tells the other open tabs of this browser with a `BroadcastChannel` message. Each tab clears its data and goes to the signed-out page. A tab with words that only its memory holds first shows those words alone, with a Copy, and goes when the person selects `Leave this page`. A tab with no such words hides its page and goes with no wait. While the provider of the first tab has not returned, this page has the note of the next section. If the logout fails, the other tabs stay as they are.

The browser can keep a page in its back-forward cache, and Back then shows this page with no load. A page hides itself when it enters this cache. After a restore, it stays hidden until `/auth/me` answers:

| The answer | The page |
|---|---|
| The session of this page | Shows itself again, with no new load |
| No session | Goes to the signed-out page |
| A newer session of the same person | Loads again. Words that only the memory of the page holds show alone first, under `Your session ended` |
| The session of another person, for example of the next person on this device | Goes to `/auth/login?anotherPerson=1` at once, so the words of this page stay hidden. With words that only the memory held, the login page says that they are gone |
| A failed call, or no answer in 3 seconds | Loads again, and shows the alert of the session check if the call still fails. Words that only the memory of the page holds show alone first, under `We could not check your session`. The check of the last person of this browser still sends the page to the login at once when another person signed in here. With no network, the browser shows its own error page, because Recall has no offline page |

So in a live session, Back from this cache costs one call of `/auth/me`.

Each answer of the Server has `Cache-Control: no-store`, also an error answer and the HTML of the app at each address. `UseNoStoreAnswers` sets this in one place, and a new endpoint gets it with no code. Only two kinds of answer do not have it, and neither holds private data: a static file of the app other than `index.html`, and an error that Kestrel or the host filter answers before the pipeline starts. So after a logout, the HTTP cache of the browser has no private answer that a page can read.

This header changes Back in Chromium. With it on each answer, Chromium does not restore a page, also in a live session, so it loads the page again on each Back. Input that was not saved can then be lost (inferred, not measured). Chromium reports three reasons: `MainResourceHasCacheControlNoStore`, `JsNetworkRequestReceivedCacheControlNoStoreResource`, and `BrowsingInstanceNotSwapped`. The build with `no-store` only on the HTML still restored the page, so the reason that decides is the one for the `fetch` of the page (inferred). Each page of the app calls `/auth/me`. WebKit does not do this: a WKWebView restored the page in a live session, so the hide above still acts there.

These are local runs of 8 Backs in a live session, in Chromium with the cache on:

| The answers | Chromium restored the page | Median time of Back | Requests of the page on one Back |
|---|---|---|---|
| `no-store` only on the HTML, over HTTP, 2 sets | 16 of 16 | 33 and 33 ms | 2 |
| `no-store` on each answer, over HTTP, 2 sets | 0 of 16 | 67 and 63 ms | 20 |
| `no-store` on each answer, over HTTPS, 1 set | 0 of 8 | 124 ms | 20 |

These runs are on localhost. The page also counts a request that the HTTP cache of the browser answers. Over HTTP, 4 of the 20 reached the Server: the HTML, `/auth/me`, and two API calls. On a network, the time of Back is the time of a page load (inferred).

Each count in the next table is the number of local runs in Chromium, with the cache on, that had private text on a frame after Back:

| The case | No `no-store` | `no-store` only on the HTML | `no-store` on each answer |
|---|---|---|---|
| All cookies removed while the page waits, over HTTP | 2 of 8, and 3 of 8 in a second set. Chromium restored the page | 0 of 8. Chromium did not restore the page | 0 of 8. Chromium did not restore the page |
| All cookies removed while the page waits, over HTTPS | 4 of 8. Chromium restored the page | 0 of 8. Chromium did not restore the page | 0 of 8. Chromium did not restore the page |
| The session revoked on the Server, with the cookie still in the browser, over HTTP | 3 of 8. Chromium restored the page | 4 of 8. Chromium restored the page | 8 of 8, from a new session. Chromium did not restore the page |
| The session revoked on the Server, over HTTPS | Not measured | Not measured | 8 of 8, from a new session. Chromium did not restore the page |
| A logout in this browser, over HTTP | 0 of 8. The message of the logout removed the page from the cache (inferred) | 0 of 8. Chromium did not restore the page | 0 of 8. Chromium did not restore the page |

- With `no-store` on each answer, a revoke shows no frame of the old page in Chromium, because Back does not restore it. The load then acts as a visit with no session. While the session at the provider lives, the person is signed in again with no click, and the private text in the table is from this new session. A plain visit and a reload do the same in this state: 4 of 4 runs each ended in a new session, on the build with `no-store` only on the HTML and on the build with it on each answer. Back on the build with `no-store` only on the HTML went to the signed-out page in 4 of 4 runs. Back on the build with it on each answer ended in a new session in 4 of 4 runs.
- The expiry of a real cookie is not measured, so the table does not say what Chromium does at that expiry.
- With the cookies gone, Back loads the page again, and this load acts as a visit with no session. In the runs, it ended at the sign-in page of the provider, not at the signed-out page, because the removal also took the cookies of the provider. If the session at the provider still lives, this load can sign the person in with no click (inferred from the visit rules of the next section, not measured).
- After a logout in this browser, Back loads the page again with no session. In the runs, the provider returned at once and had no session, so it asked who the person is. While the provider has not returned, the mark of the next section holds the no-click login. With a provider that has no endpoint, this load can sign the person in again with no password, as the next section says (inferred, not measured).
- A WKWebView run of each case over HTTP: in a live session it restored the page. After a logout, and after the cookies were removed, it restored the page and went to the signed-out page. The snapshots after the logout and the removal had no private text. A snapshot can draw a new frame, so it does not prove what the screen showed in this time.

Other browsers and other devices stay signed in. Recall has no back-channel logout, so a logout at the provider does not end their Recall sessions. `/auth/sessions/revoke-others` ends them, and they end at their own expiry.

### The session at the provider

The mark holds the login mode and a new random `state` until the provider returns to `/auth/signed-out` with that `state`. A link from another site does not know it, so it cannot end the note or start a login with no click. A provider that does not send `state` back gets the row for `did not return`. What the signed-out page says depends on this:

| The state | The signed-out page | A visit with no click |
|---|---|---|
| The provider returned | `You are signed out`, with no note | Goes to the provider, which has no session and asks for a password |
| The provider has the endpoint and did not return, for example the person left its confirm page, the provider stays on its own page after the logout, the provider refused the return URL, or the first tab is still at the provider | `You are signed out`, says that Recall cannot tell if the session there ended, and links to `Sign out of {provider}`. The note does not say that the provider failed, because a second tab reads it while the first tab is still at the provider | Shows the same page and starts nothing, until the mark ends or the next login. A provider that never returns shows this note after each logout, also to a person who did sign out there |
| The provider did not answer | `You are signed out`, says that the session there can still be open, and `Wait a few minutes, then try again`. | Shows the same page. `Try again` finds the link when the provider is back. If another provider is ready, the page has its button and no `Try again`, and a reload finds the link |
| The provider has no endpoint | `You are signed out`, says that the session there stays open, and tells the person to sign out on the site of the provider or to delete the cookies of this browser | Goes to the provider, which signs the same person in again with no password. With more than one provider, the page shows the note until the mark ends, then the usual sign-in page |

`/auth/signed-out` redirects only to the signed-out page, and the return URL that the Server sends to the provider is the origin of the request plus that path. So neither is an open redirect. Most providers also check the URL against their own list, but ZeroFactor does not.

Recall keeps no provider token, so the logout URL has `client_id`, `post_logout_redirect_uri`, and `state`, and no `id_token_hint`. OpenID Connect tells a provider to ask the person to confirm in this case. These are the results with each provider of the lab:

| Provider | `end_session_endpoint` | With no `id_token_hint` |
|---|---|---|
| ZeroFactor | Yes | Ends the session and returns at once, with `state`. It accepts any return URL. |
| Keycloak 26 | Yes | Shows `Do you want to log out?`, then returns, with `state`. Back from the signed-out page shows this page again, with no session behind it, and a second Back shows its sign-in page. With a return URL that is not registered, it shows `Invalid redirect uri`, and its session stays. |
| FusionAuth 1.69 | Yes | Ends the session and returns at once, with `state`. The return URL must be an authorized redirect URL of the application. |
| Authentik 2026.8.5 | Yes | Ends the session and stays on its own page, because it needs an `id_token_hint` to return. When it has a registered post-logout URI, it refuses a return URL with no hint, so do not register one for Recall. The next visit to Recall shows the note. |
| Duende demo | Yes | Asks the person to confirm. After `Yes`, it ends the session and stays on its own page, with no way back to Recall. The next visit to Recall shows the note. |
| Authelia 4.39 | No | Its session stays, and the signed-out page says so. |
| Cognito | No | Its session stays, and the signed-out page says so. The stack runs Cognito in the Floci emulator, whose discovery document has no such endpoint. |

These results come from a run with Recall.

## The user of a session

`GET /auth/me` tells who is signed in. The SPA and a native app read this one route.

```json
{ "sub": "<identifier>", "name": "Ada Lovelace", "sid": "<session ID>", "client_kind": "web", "login_name": "ada" }
```

`name` is the full name of the user. `client_kind` is `web`, `native`, or `pat`. `login_name` is the login name of the password of the user, or `null` for a user with no password, so the SPA offers the change of a password only to a person who has one. For a personal access token, `sid` is the ID of the token.

| Request | Answer |
|---|---|
| A live session | 200 with the shape above. A web session also extends its lifetime. |
| No credential, or a cookie with `X-CSRF: 1` that has no live session | 204. The SPA goes to the login. |
| A bearer token that is not a live session | 401, so a native app knows to refresh. |
| A cookie with no `X-CSRF: 1` | 401. The session stays, and the SPA shows an error in place of a redirect loop. |

## Data Protection keys

The Server keeps its ASP.NET Core Data Protection keys in the `keys` directory next to the database, which is `/data/keys` in the image. A backup of `/data` holds them. Nothing depends on them today: a session is an opaque value with a row in the database, and it survives the loss of the keys. A later change encrypts API keys with them, so they persist now. The directory and the files are owner-only. The Server does not encrypt the key files themselves, so at each new key the framework logs that it has no XML encryptor. This is expected.

## First login and the Owner

The first login of a new database adds the tenant `Recall` and a user with the role Admin. This person is the Owner. Two first logins at the same time cannot both win, because the database admits one first tenant.

Each later login finds the user through the pair of issuer and subject in `auth_external_logins`. An identity with no link gets the page `This server does not accept new people` with status 403, and it gets no session. The action `Use another account` starts a new login with `prompt=login`, so a person with a session at the provider under the wrong account can change it. In Keycloak, the form names the last account, and `Restart login` changes it. To let new people in, set `Auth:OpenSignup` to `true`. Each new person then joins the oldest tenant with the role User.

A user that an Admin adds through the API has no link, so that user cannot sign in with a provider until they link one. The Server makes the identifier of that user its ID, as for a user that a login adds. With password login, that user signs in as [Password login](#password-login) says, and then links a provider in settings, as [Link a provider](#link-a-provider) says.

### Link a provider

A person links an identity at a provider to their own user from settings. The Server never links by a matching email, and an Admin cannot link an identity for another person ([ADR 0025](adr/0025-admins-set-login-providers-in-the-app.md)).

1. `Link {provider}` posts to `/auth/oidc/{id}/link` with `X-CSRF: 1`. Only a web session that began at most 15 minutes ago starts a link, so a stolen cookie cannot add a way in that outlives it. An older session gets 403 with `Sign in again, then link the provider within 15 minutes.`, and the page offers a sign-out. A slide does not move the start of a session. A native session, a personal access token, an agent, and a provider token get 403. A native app links through the web: the person signs in to Recall in a browser and links there.
2. The Server writes a login attempt that holds the ID of the session, sets the binding cookie, and answers with the URL of the provider with `prompt=login`, so the person signs in there as the account they mean to link. A provider whose discovery document lists `prompt_values_supported` without `login` cannot link, and the post gets 409.
3. The provider returns to `/auth/oidc/callback`. The session cookie is `SameSite=Strict`, so the browser holds it back on that cross-site return. The callback answers one page of Recall that loads the same callback again from this origin, with no script, and that load carries the cookie.
4. The binding cookie and the live session of the request must be those of the start. Another browser, another session, or a session that ended adds no link, and the page goes to `/settings?link=refused`.
5. The callback adds the row for the issuer and the subject to the user of the session, with `display_label`, the email, else the name, that the provider gave. It starts no session and goes to `/settings?link=linked`. An identity that is already linked to this user changes nothing and also gives `linked`. An identity that is linked to another user changes nothing and gives `link=conflict`. A provider that fails gives `link=failed`.

`GET` and `DELETE /api/v1/me/external-logins` list and remove the links of the caller, in a web or native session. `GET` and `DELETE /api/v1/users/{id}/external-logins` do so for another user of the tenant, with `manage_user_access` and each permission that the user holds, as a role change needs. The body of a delete names the `issuer` and the `subject`. A link that is gone also gives 204. A removal of the last way in of the last Admin who can sign in gets 409 with the message of the last-Admin rule, under the exclusive lock of the tenant. A personal access token, an agent, and a provider token get 403 on each of these routes.

A link records the audit action `external_login_linked` on the user, and a removal records `external_login_removed`. Each holds the issuer, the subject, and the label, and no secret. The subject and the label are sensitive, as `User.Identifier` is, so only a reader with `read_audit_log_sensitive` sees them.

A tenant keeps an Admin who can sign in and manage access. Such an Admin holds each Admin permission, because a caller can give only what the caller holds. A delete, a demotion, or the removal of a permission from an Admin gets 409 when it would leave the tenant with no Admin who holds each permission, or would remove the last such Admin with a link. To remove such an Admin, first give another person who has signed in the role Admin and each Admin permission. With closed signup, set `Auth:OpenSignup` to `true` and restart the Server while that person signs in, then set it back and restart. A link counts only while a provider with its issuer is on the login page: an entry of `Auth:Providers`, or a Usable provider from the app. So a link to a provider that you removed does not count, and the delete of a provider from the app gets 409 when it would leave a tenant with no Admin who can sign in. That delete holds the lock of each live tenant. An entry that you remove from `Auth:Providers` takes effect at the restart with no check, so the owner claim recovers that case: it gives the oldest Admin the new link and each Admin permission.

A repeat login fills only a stored value that is empty. A changed value stays. A value that a person clears comes back from the provider at the next login.

### Recover a lost Owner

A changed issuer, a new provider, a provider restored with a new `sub`, or a deleted Owner can leave a person with no way to sign in. Do not delete the data directory to fix this, because it holds the journal.

1. Prepare the sign-in, so that you can finish it quickly. The claim goes to the first login that comes after the file, and any login with no link can be that login. If people that you do not know can reach the provider, limit the client at the provider to your own account, or stop the public route to Recall, until you finish.
2. Create an empty file named `claim-owner` in the data directory. It works for 15 minutes from its write time.
3. Sign in with the identity that you want.

The Server links that identity to the oldest live Admin of the oldest live tenant whose identifier is its own ID, gives that Admin each Admin permission back, logs a warning with the user ID and the issuer, and removes the file. If the tenant has no such Admin, the Server adds a new Admin. This also recovers an install where the delete of a tenant left only a tenant with no Admin.

The claim cannot bring back a deleted tenant, so `DELETE /tenants/{id}` gives 409 for the last live tenant. Only a change outside the API can leave an install with no live tenant. When a tenant row stays but is marked as deleted, a login with no link gets `signup_closed`. A login with a link still signs in when only the tenant row changed and the user is live, and it gets `signup_closed` when the user is gone too. When no tenant row stays, the Server reads a new install, and the next login with no link becomes the Owner of a new tenant. The Server removes the file in each case. Restore the data directory from a backup.

The claim is also the recovery path for a missing permission. A release that adds an Admin permission gives it to each Admin who exists with a migration (see [Add a permission](backend.md#add-a-permission)). If no Admin holds each Admin permission, for example after a change to the database outside the Server, each change to an Admin gets 409. To give each Admin permission to an Admin who can sign in, create the file and sign in with that login. The Server gives that Admin each Admin permission back, logs a warning that names the file and the user ID, and removes the file.

### Set a new Owner password

With password login, the same file sets the login name and the password of the Owner:

1. Create an empty file named `claim-owner` in the data directory.
2. Open `/auth/login`. While the file is a regular file less than 15 minutes from the current time, the page shows `Set a new Owner password`. With no such file, the page is the same as before the file. With a single provider, the page shows the button of the provider and does not start it with no click.
3. Give a login name and a password of at least 15 characters.

The post removes the file, and only one post can use one file. The Server picks the same Admin as the claim of an OIDC login, or adds an Admin when the tenant has none. It gives that Admin each Admin permission back and a Chosen password with the login name, from any state of the old password, also Stopped. It ends each session and each personal access token of that Admin, logs a warning that names the file and the user ID, records the audit action `owner_password_set`, and signs you in on this browser. An app start of the login page does not offer the claim. When the file is gone, too old, a link, or a folder at the post, the Server reads and hashes nothing, nothing changes, the entry stays where it is, and the login page says `The claim-owner file is gone or too old, so nothing changed.` The next OIDC login removes a file that is too old, as the list below says.

A refused post keeps the file, and the page shows the form again, when:

- the login name is not valid, or the password is too short, too long, common, or the login name or a name of the Owner,
- the hash line is full (429),
- a user holds the login name. The Server checks this under the lock of the tenant, just before it removes the file.

A post uses up the file and changes nothing in these cases, and when the request fails after the check, for example when the client goes away. Create the file again to try once more.

- No live tenant exists.
- A user takes the login name after the check and before the save (409). This needs a write in another tenant at the same moment.
- The save loses a race with another write of the password 3 times in a row (429). The Server logs a warning that names the file.

The first login after you create a regular file removes it when the Server can, also when the claim changes nothing:

- A login with a link to a person who is not an Admin, or to an Admin who holds each permission, signs in as usual and logs a warning that names the file. To claim the Owner, create the file again and sign in with the new identity.
- If the Server cannot remove the file, the claim does nothing and the Server logs a warning that names the file. This can occur when the file is immutable or its folder is read-only. The login adds no file and goes on as if no file exists, so a person with a link can sign in and a person with no link gets the usual answer. Make the file and its folder writable for the Server, remove the file, and create it again.
- A symbolic link or a folder named `claim-owner` is not a claim, also when the link points to a new file. The Server does not follow the link. It logs a warning that names the entry at each login, leaves the entry in place, and the login goes on as if no file exists. Remove the entry and create a regular file.
- A file with a write time more than 15 minutes from the current time, in either direction, does nothing. The Server logs a warning that names the file. The login goes on as if no file exists.

The Server removes a file with such a write time and does not leave it, because a backup restore that does not keep the write time would make a file that stays new again.

### Delete old development data

Before this design, `User.Identifier` held the provider subject. These users have no link, so the first login on an old database gets `signup_closed`. No install needs to keep this data. Stop the stack and delete the data directory: `.data` of the checkout, or `RECALL_DATA_DIR`.

## Origins

One install can answer at two origins, for example `http://192.168.1.10:8080` and `https://recall.example.com`. No public origin setting exists. The Server computes the origin of each request:

- The scheme comes from `X-Forwarded-Proto` or from the connection.
- The host comes from `Host`, and never from `X-Forwarded-Host`.

The redirect URI is that origin plus `/auth/oidc/callback`. The Server stores it on the attempt and uses it again at the redemption, so one login never mixes two origins. A session is separate for each host name, so the same person has two sessions and one user.

### Register the callback URLs

Register the callback URL of each origin at the provider. `/auth/status` gives the URL of the origin that you call.

If the provider has an `end_session_endpoint`, also register `/auth/signed-out` of each origin as a post-logout redirect URI, for example `https://recall.example.com/auth/signed-out`. In Keycloak, this is `Valid post logout redirect URIs` of the client. Without it, Keycloak refuses the logout. The Recall session ends, the session at Keycloak stays, and the person sees the error page of Keycloak. This is a dead end for each logout until you register the URL. The error page has no way back to Recall, and `Sign out of Keycloak` on the signed-out page goes to the same error. Recall cannot see the answer that the provider gives to the browser, so it cannot warn you. Register the URL before you upgrade from a version with no provider logout. If a person is already at this dead end, register the URL, and then tell the person to open Recall and select `Sign out of Keycloak`. The Server does not need a restart. Authentik is the exception: do not register a return URL there, as [The session at the provider](#the-session-at-the-provider) says.

Do not register a wildcard redirect. The Server holds the verifier and the client secret, so the Server can redeem a stolen code. A wildcard at the provider and a forged `Host` then give an attacker a session. An exact match at the provider stops that. To stop it also at the Server, set `AllowedHosts` to your host names. A request with another `Host` then gets a 400.

### Keep Host in a proxy

A TLS proxy must send the original `Host`, and it must set `X-Forwarded-Proto`. In nginx:

```nginx
proxy_set_header Host $host;
proxy_set_header X-Forwarded-Proto $scheme;
```

If a proxy changes `Host`, the provider refuses the login with a `redirect_uri` error, because the URL names the inner host.

The Server accepts `X-Forwarded-Proto` from any source. A spoofed value on plain http gives only a `Secure` cookie that the browser refuses, so it harms only the sender.

### Cookies and ports

Browsers do not isolate cookies by port (RFC 6265 section 8.5). On `http://LAN-IP`, each other app on that IP gets the cookie, and two Recall instances on one host log each other out. Plain http on a shared host is already open to a neighbor. To isolate Recall, give each app its own host name with TLS.

## Login providers

A provider comes from one of two sources. An entry under `Auth:Providers` is read at boot. A provider that an Admin adds in the app is a row of `auth_oidc_providers`, and it changes the login page at the next request, with no restart. The login page lists each entry, then each Usable row in the order in which they were added. Both kinds run the same login. The rules are in [the spec](specs/login-providers.md), and [ADR 0025](adr/0025-admins-set-login-providers-in-the-app.md) gives the reasons.

As environment variables, the keys of an entry are `Auth__Providers__0__Issuer` and so on.

| Setting | Content |
|---|---|
| `Id` | The name in the URLs. It has letters, digits, dashes, or underscores. The default is `oidc{position}`. |
| `DisplayName` | The name on the pages |
| `Issuer` | The issuer URL |
| `ClientId`, `ClientSecret` | The confidential client at the provider. Both are required. The provider must sign its ID tokens with RS, PS, or ES keys. |
| `AllowHttp` | Must be `true` for an http issuer |
| `CaCertificatePath` | A file with the certificates of a private CA |

A typo stops the boot with a message that names the setting: a repeated ID, a repeated issuer, a missing client, a missing CA file, or an http issuer with no `AllowHttp`.

### Providers from the app

A person with `manage_user_access`, in a web or native session, manages the providers under `/api/v1/login-providers`. A personal access token, an agent, and a provider token get 403. A provider is install-wide, so an Admin of any tenant manages each provider of the install. A row takes the same fields as an entry, and the same checks give 422 with the field. Two providers of either source cannot share an ID or an issuer, which gives 409. The issuer of a row cannot change: add another provider, and each person links again.

`POST /api/v1/login-providers/test` runs discovery against the issuer of the form, with its CA and `allowHttp`, and saves nothing. The list gives the callback URL to register at the provider.

An Admin chooses the issuer of a row, so its client checks each address that it connects to, also after a redirect. It refuses a loopback address, a link-local address such as `169.254.169.254`, and an unspecified address such as `0.0.0.0`, so an issuer cannot reach this server or a cloud metadata service. Discovery then fails, and the test answers that the server cannot reach the issuer. A private range, such as `192.168.0.0/16` or an IPv6 ULA, works, because an IdP on the LAN is the common case. An IdP on the same machine needs `Auth:AllowLoopbackProviders` set to `true`. An entry of `Auth:Providers` has no such check, because the operator wrote it. The issuer of a row has no query and no fragment, and the Server stores it with no trailing slash. A CA file of a row or an entry must be a regular file of at most 1 MiB.

The client secret of a row is under the Data Protection key ring, with its own purpose, and no answer, log, or audit entry holds it. An update with no secret keeps the stored one. A row has one of three states, and none is stored:

| State | When | Login page |
|---|---|---|
| Usable | Otherwise | Listed |
| Secret unreadable | The key ring cannot read its secret, as after a restore with no `keys` folder. `KeyRingWatch` then warns at each start. | Not listed, and its start gets 404. Enter the secret again. |
| Replaced by the environment | An entry of `Auth:Providers` has its ID or its issuer. The entry wins. | The entry is listed in its place |

An add, an update, and a delete record the audit actions `LoginProviderAdded`, `LoginProviderUpdated`, and `LoginProviderDeleted` on the tenant of the Admin. The entry belongs to the tenant of the Admin who made the change. The audit log is append-only, so a delete of that tenant keeps the entry, but no reader sees it after that, because a read shows only the entries of the tenant of the caller. A Usable row counts as a provider for the setup, as an entry does: the Server makes no setup code, and the setup page gets 404.

A save or a delete reads the rows again and replaces the list in one step. A row whose settings did not change keeps its mode and its discovery cache. A login in flight whose provider left the list ends at its callback with `invalid_response`. A replaced mode is never disposed, because a login in flight can still hold it. Its HTTP client closes its idle connections after the idle time of its pool. When the reload after a delete fails, the Server still takes that provider off the list and logs the failure. When the reload after an add or an update fails, the request gets 500 with the row saved, and the next save or start lists it.

Other settings:

| Setting | Default | Content |
|---|---|---|
| `Auth:OpenSignup` | `false` | See [First login and the Owner](#first-login-and-the-owner) |
| `Auth:StartPermitsPerWindow` | 20 | How many login starts the Server takes from one connection address in each 10 seconds. See [Limit on login starts](#limit-on-login-starts). |
| `Auth:AllowLoopbackProviders` | `false` | `true` lets a provider from the app connect to a loopback, link-local, or unspecified address. See [Providers from the app](#providers-from-the-app). |
| `AllowedHosts` | `*` | See [Register the callback URLs](#register-the-callback-urls) |

### Limit on login starts

`/auth/oidc/{id}/start` and each `/auth/password/*` post keep one bucket for each connection address, and one for each IPv6 `/64`. The login page and the setup page keep buckets of their own with the same limit, so opening a page takes no start from the address. So a stranger who floods the route blocks only that stranger. Another start gets 429 with a `Retry-After` header and the page `Too many sign-in attempts`.

The Server reads only the address of the connection, and never `X-Forwarded-For`, because a client can write any value in that header. Behind a proxy, each connection comes from the proxy, so all clients share one bucket and one flood blocks each new login. Some Docker networks do the same to a published port. To limit each client behind a proxy, set the limit at the proxy. A session that exists keeps working in each case.

### Private CA

`CaCertificatePath` makes a trust store for the HTTP client of one provider. It changes nothing on the machine, and no other call trusts that CA. The Server has no mode that trusts each certificate.

### No provider

With no entry under `Auth:Providers`, and no `Auth:Authority`, the Server boots, runs its migrations, and offers only the password login. See [Password login](#password-login). An API call with no session gets 401.

## Password login

The rules are in [the spec](specs/password-login.md), and [ADR 0024](adr/0024-recall-signs-people-in-with-its-own-passwords.md) gives the reasons. The login page shows the password form when the install has no provider, or when a live user holds a password. Then it also shows a button for each provider, and a single provider does not start with no click.

1. While the install has no provider and no live tenant, the Server writes one line at boot with a setup code of 20 characters, to the console and to `/data/logs`, and `/auth/status` gives `setupRequired: true`. Read the line with `docker compose logs`. A restart makes a new code. An install with a provider makes no code, `setupRequired` stays `false`, and the first OIDC login makes the Owner, as [First login and the Owner](#first-login-and-the-owner) says.
2. Open `/auth/password/setup`, and give the code, your name, a login name, and a password of at least 15 characters. The setup makes the tenant `Recall` and you its Owner, signs you in, and voids the code.
3. To add a person, an Admin adds the user with `POST /api/v1/users`, opens the page of that user in Recall, and chooses `Set temporary password`. The first time, the Admin gives the login name that the person types. The Server makes a password of 4 words that works for 7 days, shows it once, and ends each session and personal access token of that user. An Admin who sets their own keeps the session of that page. The Admin gives the password to the person outside Recall. A person who forgot a password, or whose password stopped, gets a new one the same way. The route is `POST /api/v1/users/{id}/temporary-password`, and it needs `manage_user_access` in a web or native session.

   The add request has no identifier. The Server makes the identifier of the user its ID, because a session puts the user ID in `sub` and the Server finds the caller by identifier. Before this rule, the request gave the identifier. Such a user can sign in and change the password, but each route that needs a signed-in person gives 403 `Only a person signed in to Recall can do this.` The Server does not change the identifier of such a user, because a provider token, an agent, or an entry of the audit log can name it. To fix such a user, delete the user and add the person again. The delete removes the journal of that user, so do this only for a user with no journal.
4. The person signs in at `/auth/login` with the login name and the temporary password. The Server shows only the page `Pick your password`, and the person types a new password of at least 15 characters that is not the temporary one. The Server stores it, signs the person in, and the temporary password stops working.
5. Each later sign-in is at `/auth/login`. A native app opens `/auth/login?client=app` with the values of [Sign in](#sign-in), and the page redirects to the app with a code after a right password, with no confirm page. `/auth/token` has no `password` grant.

Each form post needs the login attempt that its page wrote, and the binding cookie of that attempt. A post uses its attempt, so a page that shows the form again writes a new one. The form body has a limit of 8 KiB. The limit of login starts covers each form post. The login page and the setup page have a limit of their own, with the same buckets and permits, so a page draws nothing from the starts. After 5 wrong passwords in a row for one typed login name from one address, that pair waits 30 seconds, doubling to 15 minutes. The 100th wrong password in a row stops password sign-in for the account. A logout of a password session goes to the signed-out page with no step at a provider.

### Change a password

A person with a password changes it in the settings of the SPA. The SPA posts a form body with `currentPassword` and `newPassword` to `/auth/password/change`, with the session cookie and `X-CSRF: 1`. A native app sends its bearer token. The answer is 204. In the save of the new password, the Server ends each other session and each personal access token of the person, deletes each login in flight of the person, and keeps this session. A change that loses a race to another write of the password, such as an Admin who issues a temporary password, runs again on the new row, so the temporary password stays and the change gets 409. A refusal is a problem whose `errors` names the field:

| Status | Field | Cause |
|---|---|---|
| 403 | `currentPassword` | The current password is wrong. It counts as a wrong password for the login name and the address, and for the 100 tries in a row, the same as a wrong login. The 100th try stops the password, and the sessions keep working. |
| 422 | `newPassword` | The new password breaks a rule of length, or is on the list of common passwords, or is the login name or a name of the person. |
| 429 | none | The wait after wrong tries, or the hash line is full. The answer has `Retry-After`. |
| 409 | none | The account has no Chosen password, for example a temporary one. |

### A provider that is down

The Server asks a provider for its discovery document at most once in 2 seconds, with a 3 second limit. `/auth/status` then gives the mode the state `unreachable`, and `start` answers 503 with `idp_unreachable`. With each provider down, the login page answers 503 and offers `Try again`. `Try again` on the page of a `start` repeats that `start`, so a native app keeps its flow. Each session keeps working.

Each answer of a provider (discovery, token, keys, and userinfo) has a limit of 1 MiB of decoded bytes, so a compressed answer counts at its full size. An answer over the limit reads as a bad answer. Discovery shows the provider as `unreachable`, a token or key answer refuses the login, and a userinfo answer leaves the login on the ID token. Each failed answer logs one warning that names the provider, the answer, and the status or the cause. The warning holds no token and no body.

## File modes

The Server sets the umask to `0077` as its first step, so a new database file or stored file is readable only by its owner. At startup it also checks the data root, the database files, and the file store root. It sets a directory to `0700` and a file to `0600`, and it skips a link.

On a NAS, the user of the container and the owner of the volume can differ. SMB and some volume drivers ignore a mode change. In these cases, the Server logs one warning that names the path and the volume, and it goes on. It never changes an owner and it never refuses to boot because of a mode. To correct the warning, run the container as the user who owns the volume, or make the volume private to that user on the NAS.

The umask covers the whole process. A backup job that reads through a group loses access to new files, and no warning tells it. Run that job as the owner, or grant the access on the NAS.

## Provider tokens

`Auth:Authority` and `Auth:Audience` turn on the JWT path, for Swagger UI, MCP, and agents. With no `Auth:Authority`, the Server registers no JWT handler and no MCP metadata. The AppHost sets both for a stack with a provider.

The default scheme chooses by the credential. A `Bearer` value with the prefix `rcl_` goes to the session handler. Another value goes to the JWT handler. With no such header, a session cookie goes to the session handler. Other Authorization schemes, such as Basic from a proxy, do not hide the cookie.

The JWT handler refuses a token whose client is `recall` or starts with `pat_`, or whose `sub` has the prefix of a user ID. It checks each of `client_id`, `azp`, and `applicationId`, and refuses the token when any of them names such a client. So a provider token cannot name itself a session or a personal access token.

The JWT handler also refuses a token with a `cnf` claim, with 401. Such a token is bound to a key of its client, and the Server does not check a proof of possession.

A provider token has a provider `sub`, and a user that a login adds has its own ID as its identifier. This has these results:

- Swagger UI still signs in. `GET /api/v1/users/me` answers with the identifier from the claims, and a read of tenant data gets a refusal.
- A delegated agent gets `UnknownClient`, because its token names no user of the app.
- A machine agent works as before.

## Auth provider in the AppHost

The AppHost holds all provider config for a local run. It gives the Server the `Auth:Providers:0` entry and `Auth:Authority`. The provider resource of the stack comes from the `AuthProvider` setting. See [ADR 0007](adr/0007-regular-repo-instead-of-template.md).

### Select a provider

The `AuthProvider` setting in `Recall.AppHost/appsettings.json` selects the provider at runtime. Its value is the name of a folder in `Recall.AppHost/AuthProviders/`, without case sensitivity. The setting has no fallback: if it is empty or not known, the AppHost stops and lists the known providers.

- To change the default, edit the setting.
- To use another provider for one run, set the environment variable. For example:

  ```bash
  AuthProvider=Keycloak dotnet aspire start --isolated --non-interactive --apphost Recall.AppHost
  ```

- To use another provider for one run, you can also give the setting as an AppHost argument after `--`:

  ```bash
  dotnet aspire run -- --AuthProvider Keycloak
  ```

The `None` provider adds no container. The AppHost then forwards `Auth:Providers:*` and `Auth:OpenSignup` from its own configuration, so with no such setting the Server boots with no login method.

The `ZeroFactor` provider is [0factor](https://github.com/c4l3b/0factor), a mock OIDC provider with no passwords. Its login page lists the test users, and one click signs in. It is the default provider. It is for local development only, so select another provider before you publish or deploy.

### Change the provider of a database that has users

A login finds a user by the issuer and the subject. A new provider has a new issuer, so its first login has no link and gets `signup_closed`. To keep the journal, use the steps in [Recover a lost Owner](#recover-a-lost-owner). An isolated run starts with an empty database, so it does not have this problem.

### Add a provider

1. Add the folder `Recall.AppHost/AuthProviders/<Name>/`.
2. In the folder, add the class `<Name>AuthProvider` that implements `IAuthProvider`. The AppHost finds the class by reflection, and `<Name>` from the class name is the value of the `AuthProvider` setting. `AddTo` adds the containers of the provider to the stack and returns an `AuthProviderConfig`.
3. Put the seed files in `seed/`, and mount them with `WithSeedFiles`. Call `WithClientUrls` on the container, and read the app URLs from the `APP_*` variables in the seed files. Register `/auth/oidc/callback` at each of them. An isolated run gives the apps random ports.
4. Add `frontend/e2e/idps/<name>.json`, with `<name>` in lowercase. Give the login form and the test user. Set `resource` to the name of the Aspire resource of the provider. Set `capabilities` to what the discovery document of the provider lists.

A unit test in `AuthProviderCatalogTests` makes sure that each provider has its folder and its e2e file.

### Delete a provider

1. Delete the folder `Recall.AppHost/AuthProviders/<Name>/`.
2. Delete `frontend/e2e/idps/<name>.json`.
3. If the `AuthProvider` setting names the deleted provider, select another provider.

`AuthProviderConfig` holds each setting that can differ between providers, with the reason in its doc comments. The doc comments of each provider class give the differences of that provider and its test users.

## Agents

An agent is an OAuth client other than this app that calls the server, usually through MCP at `/mcp`. A tenant registers each agent that may act in the tenant, and grants it permissions. The reasons are in [ADR 0011](adr/0011-agents-are-tenant-records.md).

### How the server identifies the caller

The server takes only access tokens. Most provider presets use the login client ID as `Auth:Audience`, and OIDC puts that client ID in the `aud` of each ID token. The `azp` of such an ID token names the login client, which the server counts as first party, so the server would read the ID token as a first-party user that acts as any user whose identifier is its `sub`. So the server refuses a token that names itself an ID token, a logout token, or a refresh token: `typ` `ID`, `Logout`, or `Refresh` (Keycloak), `tty` `idt` (FusionAuth), `token_use` `id` (Cognito), or an `events` claim. An Authentik ID token has no such claim, but it also has no `azp`, so the server takes it as a token of an unknown client.

The server reads the client ID of the token from `client_id`, then `azp`, then `applicationId` (FusionAuth). It never takes a missing `sub` to mean a machine. `CallerIdentityResolver` decides the caller once for each request:

| Token | Caller | Permissions | Rows |
|---|---|---|---|
| A `Bearer rcl_pat_` value of a live token whose user is live | A personal access token | Each permission that both the user and the grant of the token have | The tenant of the user. The journal of the user only with journal access in the grant |
| A first-party client and a `sub` | The user | The `user_permissions` rows of the user | The tenant of the user |
| A client on a `Tenancy` allow-list | An all-tenants client | Each permission for `ManageAllTenantsClientIds`, none for `ReadAllTenantsClientIds` | All tenants |
| A client with a machine agent | A machine agent | The grant of the agent | The tenant of the agent |
| Another client, and the `sub` of a user of the app, with a delegated agent for the client in the tenant of the user | A delegated agent | Each permission that both the user and the grant of the agent have | The tenant of the user |
| Any other token, also a token with no client ID | An unknown client | None | None |

The server applies the rows in this order, and the first match wins. The first-party clients are `recall`, the client of each web or native session, `Auth:ClientId`, which Swagger UI uses, and `Swagger:ClientId` when it is set. A web or native session has the client `recall` and the user ID as its `sub`. A personal access token has its `pat_…` ID as its client. A provider token has a provider `sub`, so it matches no user. See [Provider tokens](#provider-tokens) for what that does to Swagger UI and to a delegated agent.

An allow-list and a machine agent are facts about the client that the operator sets, so they match each token of the client, whatever its `sub`. Thus a client credentials token with a service-account `sub` matches them, and a tenant cannot take such a client over with a user record that has the same `sub`.

An unknown client sees no MCP tool, and each of its calls gets a `permission_denied` tool error with the message "This client is not a registered agent of your tenant. A tenant admin can register it."

### Register an agent

1. At the identity provider, create the client of the agent. Give its tokens the API audience (`Auth:Audience`), or the server rejects them.
2. For a delegated agent, an admin of the tenant sends `POST /api/v1/agents` with `{ "name": "Clinic assistant", "clientId": "clinic-assistant", "mode": "delegated" }`. The caller needs `manage_agents`. To give a first grant, add `"permissions": ["manage_tenant"]`. With no `permissions`, the agent starts with an empty grant and can use only the read tools.
3. For a machine agent, the operator sends the same request with `"mode": "machine"` and the `tenantId`. Only a caller that can manage all tenants can add a machine agent, for example the CSharpRepl `Send` helper or a client in `ManageAllTenantsClientIds`. Bind only a confidential client that allows only the client credentials grant. If the client also allows a user sign-in, each user who signs in through it acts as the machine.
4. To grant a permission, send `POST /api/v1/agents/{id}/permissions` with `{ "permission": "manage_tenant" }`. The caller must have the permission. To take it away, send `DELETE /api/v1/agents/{id}/permissions/{permission}`. That needs only `manage_agents`, so an admin can always stop an agent. A machine agent gets its first grant from the operator in step 3 or from an admin of its tenant.
5. To remove the agent, send `DELETE /api/v1/agents/{id}`. That needs only `manage_agents`.

A delegated client ID can have one agent in each tenant, so many tenants can register one MCP client. A client ID can have one machine agent in all tenants, because the token of a machine names no tenant. A duplicate gets 409, and so does a delegated agent for a client that is bound as a machine agent. An agent for a first-party client, for a client on an allow-list, or for a client ID that starts with `pat_` gets 422, because the server would never use it. The prefix `pat_` names a personal access token.

The permission guard reads the agent and its grant from the database in each check. A removed permission, a deleted agent, or a deleted tenant applies on the next request. A delegated agent does not get the exception of `Caller.RequireSelfOr`, so it cannot update its own user without `manage_users`.

### What an agent sees in MCP

Each MCP tool declares what it needs with `[ToolAccess]` (see [endpoints.md](backend/endpoints.md#tool-access)). `tools/list` shows an agent only the tools that its permissions allow, and a call to another tool gets a `permission_denied` tool error. The denial names the side that lacks the permission. For example, an admin who acts through an agent with no `manage_tenant` grant gets "This agent has no grant to manage the tenant. A tenant admin can grant it."

When you add an MCP tool:

1. Give it the `[ToolAccess]` of the check of its handler.
2. Add a functional test for a caller who sees the tool and a caller who does not, in `TenantMcpToolTests` or a test class like it.

### Audit

Each audit entry of a save records the `sub` of the token, its client ID, and the IDs of the user and the agent of the caller (see [audit-log.md](backend/audit-log.md)). A change from a web or native session records the client ID `recall`. A change by a personal access token records its `pat_…` ID. A change by an agent records the client ID of the agent. A change with no token, for example a background job, records null.

### Providers

Agents need a provider that can give a client other than the first-party client a token with the API audience.

- ZeroFactor: the seed has the delegated client `clinic-assistant` and the machine client `fleet-sync`. Both get the `aspire-app` audience. Send a token request to `{zerofactor}/realms/recall/protocol/openid-connect/token`. For `clinic-assistant`, use `grant_type=password` with `username=admin@example.com` and any password. For `fleet-sync`, use `grant_type=client_credentials` with any client secret.
- Keycloak: give the client an `oidc-audience-mapper` that includes the client `aspire-app`, as the seed in `realm-export.json` does for `aspire-app`. For a machine client, set `serviceAccountsEnabled`.
- Cognito: agents do not work. The server checks the audience in `client_id`, so it accepts only the first-party client.

### How an MCP client finds the provider

The server publishes OAuth protected resource metadata (RFC 9728) for `/mcp`. Thus an MCP client that follows the MCP authorization spec finds the provider with no settings from the operator:

1. The client sends a request to `/mcp` with no token. The server answers `401`. The first challenge in `WWW-Authenticate` is `Bearer resource_metadata="{Mcp:PublicOrigin}/.well-known/oauth-protected-resource/mcp"`. If the token is not valid, the same challenge also has `error` and `error_description`.
2. The client gets that URL with no token. The JSON document has `resource`, which is `{Mcp:PublicOrigin}/mcp`, `authorization_servers`, and `scopes_supported`.
3. The client reads the discovery document of the provider, gets a token, and calls `/mcp` with the token.

`Mcp:PublicOrigin` is the origin at which MCP clients call the server, for example `https://api.example.com`. It must be an https URL with no path, query, fragment, or user info. Plain http is allowed only for a loopback host such as `localhost`. The server stops at startup if the value is missing or not valid. The AppHost sets it to the `https` endpoint of the server. In a deployment, set it to the public URL of the server.

`authorization_servers` has `Auth:Authority`, the same value that the JWT bearer handler uses. `scopes_supported` has the scopes that Swagger UI asks for: `Auth:Scopes` if it is set, else `openid`, `profile`, and `Auth:Audience` when `Auth:UseAudienceAsScope` is `true`.

Other paths of the server do not get `resource_metadata`. The server does not support dynamic client registration, so a tenant must register each agent first (see [Register an agent](#register-an-agent)).

## Personal access tokens

A personal access token (PAT) lets a script or an MCP client call `/api/*` and `/mcp` as one person, inside a grant that the person picks when they make it. The reasons are in [ADR 0023](adr/0023-personal-access-tokens-are-auth-sessions.md), and the rules are in [the spec](specs/personal-access-tokens.md).

In the SPA, a person makes, lists, and revokes their own tokens in Settings > Personal access tokens. The page of a user also lists the tokens of that user, with Revoke where the caller may revoke one.

### Make, list, and revoke

| Route | Who | Answer |
|---|---|---|
| `POST /api/v1/personal-access-tokens` | A person in a web or native session | 201 with the token and its `value`. This answer is the only place the value appears. |
| `GET /api/v1/personal-access-tokens` | A person in a web or native session | The tokens of the caller, newest first |
| `GET /api/v1/users/{id}/personal-access-tokens` | The user, or a caller with `read_users` | The tokens of that user of the tenant. A user of another tenant gives 404. |
| `POST /api/v1/personal-access-tokens/{id}/revoke` | The user, or a caller with `manage_user_access` | 204, also for a token that has already ended |

The body of a create names the grant and the expiry:

```json
{ "name": "Laptop", "permissions": ["read_users"], "readJournal": true, "writeJournal": false, "expiresAt": "2027-02-14T23:59:59Z" }
```

- Each permission must be one that the caller holds, or the create gets 403 and makes no token. A grant may hold `manage_model_providers`.
- `writeJournal` needs `readJournal`, or the create gets 422.
- Send exactly one of `expiresAt`, a time in the future, and `"neverExpires": true`. Any other body gets 422 with "Pick an expiry date in the future, or pick Never." So a client that forgets the field does not get a token that never expires.

A grant cannot change. To get another grant, make a new token. A list holds no value and no hash. Each row has `allowedActions`, which holds `Revoke` while the token is live and the caller can revoke it. A revoked or expired token stays in the list until the sweep removes it, 30 days after it ended.

Only a person in a web or native session can make, list, or revoke tokens. Each of these routes gives any other caller 403 with "Only a person signed in to Recall can do this.": a token, an agent, and a provider token, also one of a first-party client such as Swagger UI. So a stolen token cannot make a token that lives longer. A person who signs in with a password makes a token the same way.

### Use a token

Send the value as a bearer. A token needs no `X-CSRF` header. A token value in the session cookie authenticates nothing.

```bash
curl -H "Authorization: Bearer $RECALL_PAT" https://recall.example.com/api/v1/sessions
```

An MCP client sends the same header to `/mcp`. For Claude Code:

```bash
claude mcp add --transport http recall https://recall.example.com/mcp --header "Authorization: Bearer $RECALL_PAT"
```

Or in a `.mcp.json`:

```json
{
  "mcpServers": {
    "recall": {
      "type": "http",
      "url": "https://recall.example.com/mcp",
      "headers": { "Authorization": "Bearer ${RECALL_PAT}" }
    }
  }
}
```

`tools/list` shows only the tools that both the person and the grant allow. A token works with a provider and with none, because `SelectScheme` sends each `Bearer rcl_` value to the session handler.

The token holds each permission that both its person and its grant hold, and the guard reads the permissions of the person on each check. So a revoke, an expiry, or a permission that the person loses applies on the next request. A token is not the person: it gets no exception of `RequireSelfOr` and never passes the Owner gate. With journal access `none`, it sees no journal row and no user setting. With `read`, a change of a journal row gets 403.

### What `/auth` does with a token

| Route | A token |
|---|---|
| `/auth/me` | 200 with `client_kind: "pat"` and the token ID as `sid` |
| `/auth/sessions/revoke-others` | 403. The route needs a web or native session. |
| `/auth/logout` | 204, and nothing ends. Logout ends only an `rcl_at_` bearer, a refresh token, or a cookie. |
| `/auth/token`, `/auth/login`, `/auth/oidc/*` | They read no bearer, so a token changes nothing there. |

A `revoke-others` from a session revokes each token of the user. A delete of the user or of its tenant removes each token row of the user.

### Last use

The lookup of a token is one indexed read and no write. When the last use of the token is over an hour old, the handler marks it on its own thread, with a limit of 2 seconds, and no request waits for the mark. A busy writer line or a crash loses at most one mark, so the last use can be up to an hour stale.

### Audit

A create and a revoke from the API record the audit actions `access_token_created` and `access_token_revoked` on the user, with the token ID, its name, its grant, and its expiry. So a reader can map a `pat_…` actor to a name after the row is gone. A revoke by `revoke-others` or by a user delete records no action. A write by a token records the token ID as `actor_client` and the person as `actor_user_id`.

## E2E tests

The Playwright tests in `frontend/e2e/auth-flows.spec.ts` log in through the React app, through a native flow with a loopback server, and through Swagger UI. They run against the provider of the running stack.

1. Start the stack, as shown in [Select a provider](#select-a-provider).
2. Wait until the server and the webfrontend are healthy:

   ```bash
   dotnet aspire wait server --status healthy --apphost Recall.AppHost
   dotnet aspire wait webfrontend --status healthy --apphost Recall.AppHost
   ```

3. In `frontend`, run the tests for the provider:

   ```bash
   pnpm test:e2e:stack
   ```

The script `frontend/e2e/run-against-stack.ts` reads the stack from `dotnet aspire describe`. When no stack runs, or when the server or the webfrontend is missing or not healthy, the script stops and prints the command to fix it. Then it sets these variables and runs `playwright test --config playwright.stack.config.ts`:

| Variable | Value |
|---|---|
| `BASE_URL` | The `http` URL of `webfrontend` |
| `SERVER_URL` | The `https` URL of `server`. The Playwright setting `ignoreHTTPSErrors` accepts its certificate, and the machine trusts nothing. |
| `AUTHORITY` | The `Auth__Authority` of the server. The live discovery test reads the discovery document from it. |
| `IDP` | The name of a file in `frontend/e2e/idps/`. The default is the IdP whose `resource` is in the stack, or the IdP with no `resource` if the stack has none. Set it to override the IdP. |

Extra arguments go to Playwright, for example `pnpm test:e2e:stack --repeat-each=5`.

`frontend/e2e/two-people.spec.ts` signs in two people on one browser, as in [One browser, two people](#one-browser-two-people). It needs an IdP file with `secondPersonEmail`, which only ZeroFactor has now, and a stack with `Auth__OpenSignup=true`. On another IdP it does not run, and the report says so.

`frontend/e2e/personal-access-tokens.spec.ts` makes a token in the SPA, calls the API with it from outside the browser, and revokes it in the SPA. It signs in with a one-step form, so it does not run on an IdP with a two-step login, such as Authentik.

## Conformance

Recall dropped the OpenID Foundation conformance run. The results of the template were for its BFF, which Recall deleted, so they say nothing about the login of the Server. No run in this repository tests the Server against that suite.

## Test users

The seed files of each provider create its test users. The doc comment of each provider class in `Recall.AppHost/AuthProviders/` lists them. The file in `frontend/e2e/idps/` gives the user that the e2e tests log in with.

The seed files create users in the provider only. The first test user who signs in on a new database becomes the Owner. Each other test user gets the page `This server does not accept new people`, unless `Auth:OpenSignup` is `true`.
