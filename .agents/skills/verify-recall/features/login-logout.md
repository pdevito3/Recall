# Login and logout

Each page of the SPA needs a session. A visitor with no session goes to the login at once: the SPA sends the browser to `/auth/login` with the page as `returnUrl`, and shows no page first. The Server sends the browser to the IdP and back to that page, then keeps the session in the `recall` cookie, or `__Host-recall` on TLS. The home page then shows the name of the user, which proves the path of the session: browser cookie, session row, and user row. `Logout` ends the session in the Server and shows the signed-out page of the Server. The session of the IdP stays, and that page has a link to the logout of the IdP when the IdP has one.

## Sub-features

- `auth-anonymous` goes to `/auth/login` at once, with the page as `returnUrl`. `GET /auth/me` is 204, and `GET /api/v1/users/me` is the 401 of the Server.
- `auth-login` goes through `/auth/login`, `/auth/oidc/<id>/start`, the login page of the IdP, and `/auth/oidc/callback`, back to the page that the visitor opened.
- `auth-session` shows the name in the user menu at the bottom of the sidebar, and `Logout` in that menu. The `Account` section of `/settings` shows `Signed in as <name>`. `GET /api/v1/users/me` is 200 with the same name. The session survives a reload.
- `auth-logout` sends `POST /auth/logout` with `X-CSRF: 1`, then goes to `/auth/login?signedOut=1`. The page says `You are signed out`, and has the links `Sign in with <provider>` and `Sign out of <provider>`.

## How to get to it (user POV)

- Open any page of the app with no session. The login page of the IdP shows.
- Choose `Logout` in the user menu at the bottom of the sidebar.

## Driving it with browse.mjs

Preconditions:

- `doctor.sh` exits with 0.
- The browser context is new, so nobody is logged in.

- **Anonymous state.** Open `/`. Run `page.goto('/', { waitUntil: 'commit' })` after `waitForResponse` on `/auth/me` and `waitForRequest` on `/auth/login`. The response is 204, and the `returnUrl` of the login request is `/`. The page is then the login page of the IdP, so send each check of the app with `page.request`: `page.request.get('/api/v1/users/me', { headers: { 'X-CSRF': '1' } })` gives 401.
- **Log in.** Sign in at the IdP. Run `const { name } = await login()`. On a new page, the helper opens `/` first. At ZeroFactor, it clicks the user `[data-testid="user-admin@example.com"]`. At DuendeDemo, it fills `#Input_Username` and `#Input_Password` with `bob` and clicks `button[value="login"]`. The browser returns to the `web` origin, and the `Main` navigation of the layout is visible. `name` is the `name` field of `GET /auth/me`.
- **Session.** Read the name. Run ``page.getByRole('button', { name: `User menu for ${name}`, exact: true })``. The button is visible. `fetch('/api/v1/users/me', { headers: { 'X-CSRF': '1' } })` in the page gives 200, and its `name` is `name`. `context.cookies()` contains `recall` with `httpOnly`, `sameSite: 'Strict'`, and `path: '/'`. With `--preview`, the name is `__Host-recall`.
- **Reload.** Run `page.reload()`. The name is visible again.
- **User menu.** Click the button `User menu for <name>` in the sidebar. The menu shows the name.
- **Log out.** Run `logout()`. The helper opens the user menu, chooses `Logout`, and waits for `/auth/login?signedOut=1`.
- **Logged-out state.** Run `page.request.get('/auth/me', { headers: { 'X-CSRF': '1' } })`. The status is 204. The heading is `You are signed out`, and the title is `You are signed out - Recall`. The link `Sign out of <provider>` goes to the `end_session_endpoint` of the provider.
- **Sign in again.** Choose the link `Sign in with <provider>`. The IdP still has its session, so the same user is back with no click at the IdP.
- **Proof.** Run `node $S/scripts/browse.mjs $S/recipes/login-logout.mjs`. The captures `anonymous`, `logged-in`, and `logged-out` show the three states.

## Gotchas

- With DuendeDemo, the IdP is a public server. If it is down, login fails. The doctor examines it.
- The Duende demo server sometimes sends its CSS and scripts very slowly (17 to 29 s were seen). Thus the page `load` event can take more than 30 s. In a new login script, wait for `domcontentloaded` and for `#Input_Username`, never for `load`. `login()` does this.
- Each call to `/auth/*` and `/api/*` with the cookie needs the header `X-CSRF: 1`. Without it, the Server answers 401 even with a valid session.
- An anonymous visitor is a normal state. `/auth/me` answers 204, so `network.log` and `console.log` get no line for it.
- A recipe must log in before it opens a page of the app. Without a session, `page.goto` lands on the login page of the IdP.
- The page shows the `name` of the session (`Admin User`, or `Bob Smith` at DuendeDemo). The Server takes it from the provider at the first login.
- The pages of the Server have a strict `Content-Security-Policy` and no script. An init script that adds a style there logs a console error, and that fails the run.
- `launch.sh` sets the provider, whatever the default in `appsettings.json` is. ZeroFactor is the default. `login()` supports only ZeroFactor and DuendeDemo. Other providers use other forms and users. See `frontend/e2e/idps/` for their selectors.
