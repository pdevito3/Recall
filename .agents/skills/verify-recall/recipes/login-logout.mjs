// The page is on the IdP when the visitor has no session, so a check of the app sends its own request. The context
// sends the cookies of the app with it.
const appStatus = async (page, path) => (await page.request.get(path, { headers: { 'X-CSRF': '1' } })).status()

// A session cookie on TLS has the __Host- prefix. Plain http cannot hold such a cookie.
const sessionCookieName = (stack) => (new URL(stack.urls.web).protocol === 'https:' ? '__Host-recall' : 'recall')

export default async ({ page, context, stack, step, capture, expect, login, logout }) => {
  await step('anonymous: /auth/me is 204, the SPA goes to the login at once with / as the return URL, and the Server answers 401', async () => {
    const res = page.waitForResponse((r) => new URL(r.url()).pathname === '/auth/me')
    const loginRequest = page.waitForRequest((r) => new URL(r.url()).pathname === '/auth/login')
    await page.goto('/', { waitUntil: 'commit' })
    expect((await res).status()).toBe(204)
    expect(new URL((await loginRequest).url()).searchParams.get('returnUrl')).toBe('/')
    await page.waitForURL((u) => u.origin !== new URL(stack.urls.web).origin, { waitUntil: 'domcontentloaded' })
    expect(await appStatus(page, '/api/v1/users/me')).toBe(401)
  })
  await capture('anonymous')

  const { name } = await step('log in at the IdP', () => login())

  await step('logged in: the page shows the name of the user, and the Server holds a session for the session cookie', async () => {
    await expect(page.getByText(`Signed in as ${name}`)).toBeVisible()
    const me = await page.evaluate(async () => {
      const response = await fetch('/api/v1/users/me', { headers: { 'X-CSRF': '1' } })
      return { status: response.status, body: await response.json() }
    })
    expect(me.status).toBe(200)
    expect(me.body.name).toBe(name)
    const cookie = (await context.cookies()).find((c) => c.name === sessionCookieName(stack))
    expect(cookie).toMatchObject({ httpOnly: true, sameSite: 'Strict', path: '/' })
  })
  await capture('logged-in')

  await step('session survives a reload', async () => {
    await page.reload()
    await expect(page.getByText(`Signed in as ${name}`)).toBeVisible()
  })

  await step('the user menu shows the name of the session', async () => {
    await page.getByRole('button', { name: `User menu for ${name}`, exact: true }).click()
    await expect(page.getByRole('menu', { name: 'User menu' }).getByText(name, { exact: true })).toBeVisible()
  })
  await capture('user-menu')
  await page.keyboard.press('Escape')

  await step('log out', () => logout())

  await step('logged out: /auth/me is 204 again, and the page is the signed-out page of the Server', async () => {
    expect(await appStatus(page, '/auth/me')).toBe(204)
    await expect(page.getByRole('heading', { name: 'You are signed out' })).toBeVisible()
    await expect(page).toHaveTitle('You are signed out - Recall')
    await expect(page.getByText(`Signed in as ${name}`)).toHaveCount(0)
    await expect(page.getByRole('link', { name: /^Sign in with / })).toBeVisible()
    await expect(page.getByRole('link', { name: /^Sign out of / })).toHaveCount(0)
  })
  await capture('logged-out')

  await step('sign in from the signed-out page: the IdP has no session, so it asks again and starts no silent sign-in', async () => {
    await page.getByRole('link', { name: /^Sign in with / }).click()
    await page.waitForURL((u) => u.origin !== new URL(stack.urls.web).origin, { waitUntil: 'domcontentloaded' })
    expect(await appStatus(page, '/auth/me')).toBe(204)
  })
}
