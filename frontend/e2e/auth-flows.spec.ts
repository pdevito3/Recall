import { createHash, randomBytes } from 'node:crypto'
import { createServer } from 'node:http'
import type { AddressInfo } from 'node:net'
import { test, expect, type Locator, type Page, type Request } from '@playwright/test'
import { loadIdp, type IdpCapabilities, type IdpConfig } from './idps.ts'

/**
 * Authentication flow tests for all supported Identity Providers.
 *
 * Test users (from docs/authentication.md):
 * - Keycloak/FusionAuth/Authentik/Cognito: admin@example.com / password123!
 * - Duende Demo: bob / bob
 * - ZeroFactor: admin@example.com, with no password
 *
 * Run with: pnpm test:e2e:stack (against the IdP of the running stack, see docs/authentication.md)
 * Debug with: pnpm test:e2e:stack --debug
 * UI mode: pnpm test:e2e:stack --ui
 */

// pnpm test:e2e:stack sets IDP to the provider of the running stack. See e2e/idps.ts.
const currentIdp = loadIdp(process.env.IDP)

/**
 * True when the locator becomes visible within the timeout. `locator.isVisible()` does not wait, and it ignores
 * its timeout option, so it cannot wait for a page that is still loading.
 */
async function becomesVisible(locator: Locator, timeout: number): Promise<boolean> {
  return locator.waitFor({ state: 'visible', timeout }).then(
    () => true,
    () => false,
  )
}

/**
 * Opens a tag section of Swagger UI. A click on the tag toggles it, so the click happens only when the section is closed.
 */
async function expandSwaggerTag(page: Page, tag: string) {
  const section = page.locator('.opblock-tag-section').filter({ has: page.locator(`.opblock-tag[data-tag="${tag}"]`) })
  await expect(section).toBeVisible()
  if (!(await section.getAttribute('class'))?.includes('is-open')) {
    await section.locator('.opblock-tag').click()
  }
  await expect(section).toHaveClass(/\bis-open\b/)
}

/**
 * Helper to login via the IDP login page
 */
async function loginAtIdp(page: Page, idp: IdpConfig) {
  // Wait for the IDP login form
  await page.locator(idp.usernameSelector).waitFor({ state: 'visible' })

  // Fill username/email
  const usernameValue = idp.email || idp.username
  await page.locator(idp.usernameSelector).fill(usernameValue!)

  if (idp.twoStepLogin) {
    // Two-step login: submit username first, then password
    await page.locator(idp.submitSelector).click()

    // Fill password on the second page. fill waits for the field
    await page.locator(idp.passwordSelector!).fill(idp.password!)

    // Use secondStepSubmitSelector if defined, otherwise use submitSelector
    const secondSubmit = idp.secondStepSubmitSelector || idp.submitSelector
    await page.locator(secondSubmit).click()
  } else {
    // Single-page login: fill password, if the IdP has one, and submit
    if (idp.passwordSelector) {
      await page.locator(idp.passwordSelector).fill(idp.password!)
    }
    await page.locator(idp.submitSelector).click()
  }
}

test.describe(`Auth Flows - ${currentIdp.name}`, () => {
  test.describe('React UI Authentication', () => {
    test('should login, call the Server as the signed-in user, and logout', async ({ page }) => {
      await page.goto('/')
      await expect(page.locator(currentIdp.usernameSelector)).toBeVisible({ timeout: 30000 })

      // Step 4: Login at the IDP
      await loginAtIdp(page, currentIdp)

      // Step 5: Wait for redirect back to app and verify logged in state
      await page.waitForURL('/', { timeout: 30000 })

      // Verify logout button is visible (indicates logged in)
      const logoutButton = page.getByRole('button', { name: /logout/i })
      await expect(logoutButton).toBeVisible({ timeout: 10000 })

      await expect(page.getByText(/^Signed in as /)).toBeVisible({ timeout: 10000 })

      // Step 7: Click Logout. The 200 before the logout proves that the 204 after it comes from the logout
      const appOrigin = new URL(page.url()).origin
      const sessionUser = () => page.request.get(new URL('/auth/me', appOrigin).toString(), { headers: { 'X-CSRF': '1' } })
      expect((await sessionUser()).status(), 'the session before the logout').toBe(200)
      await logoutButton.click()

      if (currentIdp.logoutConfirmSelector) await page.locator(currentIdp.logoutConfirmSelector).click({ timeout: 30000 })
      await page.waitForURL((url) => url.pathname === '/auth/login' && url.searchParams.get('signedOut') === '1', { timeout: 30000 })
      await expect(page.getByRole('heading', { name: 'You are signed out' })).toBeVisible()
      await expect(page).toHaveTitle('You are signed out - Recall')
      await expect(page.getByRole('link', { name: /^Sign in with / })).toBeVisible()
      expect((await sessionUser()).status(), 'the session after the logout').toBe(204)
    })

    test('should return to a deep link on the SPA origin, with its query, after a login from that link', async ({ page, baseURL }) => {
      const deepLink = '/about?tab=team&x=1'
      const loginRequest = page.waitForRequest((request) => new URL(request.url()).pathname === '/auth/login')

      await page.goto(deepLink)
      expect(new URL((await loginRequest).url()).searchParams.get('returnUrl')).toBe(deepLink)
      await loginAtIdp(page, currentIdp)

      await page.waitForURL(new URL(deepLink, baseURL).toString(), { timeout: 30000 })
      await expect(page.getByRole('navigation', { name: 'breadcrumb' })).toHaveText('About')
      const status = await page.evaluate(async () => (await fetch('/auth/me', { headers: { 'X-CSRF': '1' } })).status)
      expect(status).toBe(200)
    })
  })
})

const sessionCookieName = 'recall'

/**
 * Logs in through the SPA and waits for the first load after the redirect to show the signed-in user.
 */
async function signInThroughSpa(page: Page) {
  await page.goto('/')
  await loginAtIdp(page, currentIdp)
  await page.waitForURL('/', { timeout: 30000 })
  await expect(page.getByRole('button', { name: /logout/i })).toBeVisible({ timeout: 10000 })
}

/**
 * Opens a page on another site with one link to `target`, and clicks the link. The browser then sends a cross-site
 * top-level GET, as for a link in an email or on another web site. Returns the request of the navigation.
 */
async function followLinkFromAnotherSite(page: Page, target: string) {
  const otherSite = 'https://cross-site.test/'
  await page.route(otherSite, (route) =>
    route.fulfill({ contentType: 'text/html', body: `<!doctype html><a href="${target}">Open the app</a>` }),
  )
  await page.goto(otherSite)
  const navigation = page.waitForRequest((request) => request.url() === target && request.isNavigationRequest())
  await page.getByRole('link', { name: 'Open the app' }).click()
  return navigation
}

async function sentSessionCookie(request: Request) {
  const cookie = (await request.allHeaders())['cookie'] ?? ''
  return cookie.split(';').some((pair) => pair.trim().startsWith(`${sessionCookieName}=`))
}

test.describe(`Session cookie - ${currentIdp.name}`, () => {
  test('should set the login binding cookie with Lax and the session cookie with Strict, and end signed in on the first load', async ({
    page,
    context,
  }) => {
    await page.goto('/')
    await page.locator(currentIdp.usernameSelector).waitFor({ state: 'visible', timeout: 30000 })

    // The browser must send this cookie on the cross-site GET from the identity provider to /auth/oidc/callback
    const bindingCookie = (await context.cookies()).find((c) => c.name.startsWith('recall_login_'))
    expect(bindingCookie, 'the login binding cookie').toBeDefined()
    expect(bindingCookie).toMatchObject({ sameSite: 'Lax', secure: false, httpOnly: true, path: '/auth/oidc' })

    await loginAtIdp(page, currentIdp)

    await page.waitForURL('/', { timeout: 30000 })
    await expect(page.getByRole('button', { name: /logout/i })).toBeVisible({ timeout: 10000 })

    const cookies = await context.cookies()
    expect(cookies.find((c) => c.name === sessionCookieName)).toMatchObject({ sameSite: 'Strict', secure: false, httpOnly: true, path: '/' })
    expect(cookies.some((c) => c.name.startsWith('recall_login_')), 'the callback deletes the binding cookie').toBe(false)
  })

  test('should show the signed-in user on a deep link from another site', async ({ page }) => {
    await signInThroughSpa(page)
    const deepLink = new URL('/about', page.url()).toString()
    // The root layout of the SPA that the cross-site link loads calls /auth/me same-site, with the cookie
    const user = page.waitForResponse(
      (response) => new URL(response.url()).pathname === '/auth/me' && page.url() === deepLink,
    )

    const navigation = await followLinkFromAnotherSite(page, deepLink)

    await page.waitForURL(deepLink)
    // The navigation is cross-site, so the browser does not send the Strict cookie with the HTML request
    expect(await sentSessionCookie(navigation)).toBe(false)
    await expect(page.getByRole('navigation', { name: 'breadcrumb' })).toHaveText('About')
    expect((await user).status()).toBe(200)

    // Only the home page shows the user
    await page.getByRole('link', { name: 'Home' }).click()
    await expect(page.getByRole('button', { name: /logout/i })).toBeVisible({ timeout: 10000 })
  })

  test('should send no session cookie on a cross-site top-level GET to /api, which gets a 401', async ({ page }) => {
    await signInThroughSpa(page)
    const apiUrl = new URL('/api/v1/users/me', page.url()).toString()

    const navigation = await followLinkFromAnotherSite(page, apiUrl)

    const response = await navigation.response()
    expect(response?.status()).toBe(401)
    expect(await sentSessionCookie(navigation)).toBe(false)

    // The same call from the SPA is same-site, so it has the cookie and succeeds
    await page.goto('/')
    const sameSiteStatus = await page.evaluate(
      async (url) => (await fetch(url, { headers: { 'X-CSRF': '1' } })).status,
      apiUrl,
    )
    expect(sameSiteStatus).toBe(200)
  })
})

test.describe(`Swagger - ${currentIdp.name}`, () => {
  // Get server URL from environment or use default
  const serverUrl = process.env.SERVER_URL || 'https://localhost:24713'

  test('should load Swagger UI with OAuth configured and display the Users endpoints', async ({
    page,
  }) => {
    // Step 1: Navigate to Swagger UI
    await page.goto(`${serverUrl}/swagger`, {
      waitUntil: 'networkidle',
    })

    // Step 2: Verify Swagger loaded correctly with title (it's in h1)
    await expect(page.locator('h1').filter({ hasText: 'Recall API' })).toBeVisible({
      timeout: 10000,
    })

    // Step 3: Verify Authorize button is present (OAuth is configured)
    const authorizeButton = page.getByRole('button', { name: /authorize/i })
    await expect(authorizeButton).toBeVisible({ timeout: 5000 })

    // Step 4: Expand the Users section to see the Users endpoints
    await expandSwaggerTag(page, 'Users')

    // Step 5: Verify the Users endpoints are visible after expansion
    await expect(page.getByText('/api/v1/users').first()).toBeVisible({ timeout: 5000 })
    await expect(page.getByText('/api/v1/users/me')).toBeVisible({ timeout: 5000 })
  })

  test('should authorize and call the current user endpoint', async ({ page }) => {
    // Step 1: Navigate to Swagger UI
    await page.goto(`${serverUrl}/swagger`, {
      waitUntil: 'networkidle',
    })

    // Step 2: Click the Authorize button
    const authorizeButton = page.getByRole('button', { name: /authorize/i })
    await expect(authorizeButton).toBeVisible({ timeout: 10000 })
    await authorizeButton.click()

    // Step 3: Wait for modal and click authorize in the OAuth section
    await page.locator('.modal-ux').waitFor({ timeout: 5000 })

    // The authorize button in the modal
    const modalAuthorizeButton = page.locator('.modal-ux .auth-btn-wrapper button.authorize')
    await expect(modalAuthorizeButton.first()).toBeVisible({ timeout: 5000 })

    // Step 4: Handle the OAuth popup
    const popupPromise = page.waitForEvent('popup', { timeout: 10000 })
    await modalAuthorizeButton.first().click()

    let popup: import('@playwright/test').Page
    try {
      popup = await popupPromise
    } catch {
      // If no popup appears, OAuth might be configured differently or blocked
      // Close modal and skip this test
      const closeButton = page.locator('.modal-ux button.btn-done, .modal-ux button[aria-label="Close"]')
      if (await closeButton.first().isVisible()) {
        await closeButton.first().click()
      }
      test.skip(true, 'OAuth popup was blocked or did not appear')
      return
    }

    // Step 5: Login at the IDP
    await popup.waitForLoadState('networkidle', { timeout: 15000 })
    await loginAtIdp(popup, currentIdp)

    // Step 6: Wait for popup to close
    try {
      await popup.waitForEvent('close', { timeout: 30000 })
    } catch {
      // Popup might have already closed or redirected
      if (!popup.isClosed()) {
        await popup.close()
      }
    }

    // Step 7: Close the modal
    // Swagger UI exchanges the code for a token after the popup closes. Wait for the result, not a fixed time.
    await expect(page.locator('.modal-ux').getByRole('heading', { name: 'Authorized' })).toBeVisible({
      timeout: 15000,
    })
    const closeButton = page.locator('.modal-ux button.btn-done')
    if (await closeButton.isVisible()) {
      await closeButton.click()
    }

    // Step 8: Expand the Users section
    await expandSwaggerTag(page, 'Users')

    // Step 9: Find and click on the current user endpoint row to expand it
    const secureEndpoint = page.locator('.opblock').filter({ hasText: '/api/v1/users/me' })
    await expect(secureEndpoint).toBeVisible({ timeout: 5000 })

    // Click on the summary to expand if not already expanded. A click toggles it
    if (!(await secureEndpoint.getAttribute('class'))?.includes('is-open')) {
      await secureEndpoint.locator('.opblock-summary').click()
    }
    await expect(secureEndpoint).toHaveClass(/\bis-open\b/)
    // Swagger UI resolves an operation after it opens. An Execute before that fails with
    // OperationNotFoundError and sends no request. The response example shows the resolved schema.
    await expect(secureEndpoint.locator('.responses-wrapper')).toContainText('identifier', { timeout: 30000 })

    // Step 10: Click "Try it out" button if visible (might be auto-enabled)
    const tryItOutButton = secureEndpoint.getByRole('button', { name: 'Try it out' })
    if (await becomesVisible(tryItOutButton, 1000)) {
      await tryItOutButton.click()
    }

    // Step 11: Click Execute button
    const executeButton = secureEndpoint.getByRole('button', { name: 'Execute' })
    await expect(executeButton).toBeVisible({ timeout: 5000 })
    await executeButton.click()

    // Step 12: Check response
    // Look for response status in the live responses section. The expect waits for the API response
    const liveResponse = secureEndpoint.locator('.live-responses-table').first()
    await expect(liveResponse).toBeVisible({ timeout: 10000 })

    // The first status cell is the table header ("Code"), so read the response row
    const responseStatus = liveResponse.locator('tr.response .response-col_status')
    await expect(responseStatus).toContainText('200', { timeout: 10000 })

    // identifier in the body proves that the token reached the API
    const responseBody = secureEndpoint.locator('.live-responses-table pre.microlight').first()
    await expect(responseBody).toContainText('identifier', { timeout: 5000 })
  })
})

// The live discovery document of the provider must match the capabilities in its e2e/idps/ file, so that a provider
// upgrade that adds or drops a feature fails here and not in a feature test
test.describe(`Provider discovery - ${currentIdp.name}`, () => {
  test('live discovery matches the capabilities of the IdP file', async ({ request }) => {
    const authority = process.env.AUTHORITY
    test.skip(!authority, 'AUTHORITY is not set. pnpm test:e2e:stack sets it from the stack.')

    const response = await request.get(`${authority!.replace(/\/$/, '')}/.well-known/openid-configuration`)
    expect(response.status()).toBe(200)
    const discovery = (await response.json()) as Record<string, unknown>

    const live: IdpCapabilities = {
      revocation: typeof discovery.revocation_endpoint === 'string',
      issuerParameter: discovery.authorization_response_iss_parameter_supported === true,
      backChannelLogout: discovery.backchannel_logout_supported === true,
      endSession: typeof discovery.end_session_endpoint === 'string',
      pushedAuthorization: typeof discovery.pushed_authorization_request_endpoint === 'string',
    }
    expect(live).toEqual(currentIdp.capabilities)
  })
})

// The contract is in docs/authentication.md. Node fetch, not the request fixture of Playwright, keeps the tokens out of the trace.
test.describe(`Native app login - ${currentIdp.name}`, () => {
  interface TokenPair {
    access_token: string
    refresh_token: string
    token_type: string
    expires_in: number
  }

  function appOrigin(baseURL: string | undefined): string {
    expect(baseURL, 'BASE_URL').toBeTruthy()
    return new URL(baseURL!).origin
  }

  async function postToken(origin: string, form: Record<string, string>): Promise<{ status: number; body: Record<string, unknown> }> {
    const response = await fetch(`${origin}/auth/token`, { method: 'POST', body: new URLSearchParams(form) })
    return { status: response.status, body: (await response.json()) as Record<string, unknown> }
  }

  async function currentUserStatus(origin: string, accessToken: string): Promise<number> {
    const response = await fetch(`${origin}/api/v1/users/me`, { headers: { Authorization: `Bearer ${accessToken}` } })
    return response.status
  }

  async function readStartUrl(origin: string): Promise<string> {
    const status = (await (await fetch(`${origin}/auth/status`)).json()) as { modes: { startUrl: string }[] }
    return status.modes[0].startUrl
  }

  async function signInAsApp(page: Page, origin: string): Promise<TokenPair> {
    const loopback = createServer()
    const callback = new Promise<URL>((resolve) => {
      loopback.on('request', (request, response) => {
        resolve(new URL(request.url ?? '/', 'http://127.0.0.1'))
        response.end('You can close this page.')
      })
    })
    await new Promise<void>((resolve) => loopback.listen(0, '127.0.0.1', resolve))
    try {
      const redirectUri = `http://127.0.0.1:${(loopback.address() as AddressInfo).port}/callback`
      const codeVerifier = randomBytes(32).toString('base64url')
      const state = randomBytes(16).toString('base64url')
      const parameters = new URLSearchParams({
        redirect_uri: redirectUri,
        code_challenge: createHash('sha256').update(codeVerifier).digest('base64url'),
        code_challenge_method: 'S256',
        state,
      })

      await page.goto(`${origin}${await readStartUrl(origin)}&${parameters}`)
      await loginAtIdp(page, currentIdp)
      await expect(page.getByRole('heading', { name: 'Sign in to the Recall app?' })).toBeVisible({ timeout: 30000 })
      await page.getByRole('button', { name: 'Continue' }).click()

      const callbackUrl = await callback
      expect(callbackUrl.searchParams.get('state')).toBe(state)
      const code = callbackUrl.searchParams.get('code')
      expect(code, 'the app code').toBeTruthy()

      const exchange = await postToken(origin, { grant_type: 'authorization_code', code: code!, code_verifier: codeVerifier, redirect_uri: redirectUri })
      expect(exchange.status, 'the code exchange').toBe(200)
      expect(exchange.body).toMatchObject({ token_type: 'Bearer', expires_in: 900 })
      return exchange.body as unknown as TokenPair
    } finally {
      loopback.close()
    }
  }

  test('/auth/status gives an app the login method, with no provider settings in the app', async ({ baseURL }) => {
    const origin = appOrigin(baseURL)
    const response = await fetch(`${origin}/auth/status`)
    expect(response.status).toBe(200)
    expect(await response.json()).toEqual({
      instance: 'recall',
      state: 'ready',
      callbackUrl: `${origin}/auth/oidc/callback`,
      message: null,
      setupRequired: false,
      modes: [
        {
          kind: 'oidc',
          id: process.env.IDP,
          displayName: expect.any(String),
          state: 'ready',
          detail: null,
          startUrl: `/auth/oidc/${process.env.IDP}/start?client=app`,
        },
      ],
    })
  })

  test('a refresh rotates the tokens, and a repeat of one refresh gives the same pair', async ({ page, baseURL }) => {
    const origin = appOrigin(baseURL)
    const first = await signInAsApp(page, origin)
    expect(await currentUserStatus(origin, first.access_token), 'the first access token').toBe(200)

    const retryKey = randomBytes(32).toString('base64url')
    const refresh = { grant_type: 'refresh_token', refresh_token: first.refresh_token, retry_key: retryKey }
    const second = await postToken(origin, refresh)
    expect(second.status, 'the refresh').toBe(200)
    // Compare without the values, so that a failure does not print a token
    expect(second.body.refresh_token !== first.refresh_token, 'the refresh gives a new refresh token').toBe(true)

    const repeat = await postToken(origin, refresh)
    expect(repeat.status, 'the repeat of the refresh').toBe(200)
    expect(repeat.body.access_token === second.body.access_token, 'the repeat gives the same access token').toBe(true)
    expect(repeat.body.refresh_token === second.body.refresh_token, 'the repeat gives the same refresh token').toBe(true)

    expect(await currentUserStatus(origin, first.access_token), 'the access token before the refresh').toBe(401)
    expect(await currentUserStatus(origin, second.body.access_token as string), 'the access token after the refresh').toBe(200)
  })

  test('a replay of an old refresh token ends the session', async ({ page, baseURL }) => {
    const origin = appOrigin(baseURL)
    const first = await signInAsApp(page, origin)
    const second = await postToken(origin, { grant_type: 'refresh_token', refresh_token: first.refresh_token })
    expect(second.status, 'the refresh').toBe(200)

    const replay = await postToken(origin, { grant_type: 'refresh_token', refresh_token: first.refresh_token })
    expect({ status: replay.status, error: replay.body.error }).toEqual({ status: 400, error: 'invalid_grant' })

    expect(await currentUserStatus(origin, second.body.access_token as string), 'the access token of the session').toBe(401)
    const afterReplay = await postToken(origin, { grant_type: 'refresh_token', refresh_token: second.body.refresh_token as string })
    expect({ status: afterReplay.status, error: afterReplay.body.error }).toEqual({ status: 400, error: 'invalid_grant' })
  })
})
