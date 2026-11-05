export default async ({ page, context, step, capture, expect, login, logout }) => {
  const owner = await step('the first login makes the Owner: an Admin of the tenant Recall', async () => {
    const { name } = await login('admin@example.com')
    const me = await page.evaluate(async () => {
      const response = await fetch('/api/v1/users/me', { headers: { 'X-CSRF': '1' } })
      return { status: response.status, body: await response.json() }
    })
    expect(me.status).toBe(200)
    expect(me.body.name).toBe(name)
    return me.body
  })

  await step('the home page shows no notice of a missing tenant', async () => {
    await expect(page.getByRole('region', { name: 'No tenant' })).toHaveCount(0)
  })
  await capture('home-of-owner')

  await step('the Owner holds the permissions of the role: GET /api/v1/users lists the Owner', async () => {
    const users = await page.evaluate(async () => {
      const response = await fetch('/api/v1/users', { headers: { 'X-CSRF': '1' } })
      return { status: response.status, body: await response.json() }
    })
    expect(users.status).toBe(200)
    expect(users.body.map((u) => u.identifier)).toEqual([owner.identifier])
    expect(users.body[0].role).toBe('Admin')
  })

  await step('log out, and leave the session of the IdP', async () => {
    await logout()
    await context.clearCookies()
  })

  await step('a second person signs in: the Server refuses, because signup is closed', async () => {
    await page.goto('/', { waitUntil: 'commit' })
    const refused = page.waitForResponse((r) => new URL(r.url()).pathname === '/auth/oidc/callback')
    await page.locator('[data-testid="user-user@example.com"]').click({ timeout: 30_000 })
    expect((await refused).status()).toBe(403)
    await expect(page.getByRole('heading', { name: 'This server does not accept new people' })).toBeVisible()
    await expect(page).toHaveTitle('This server does not accept new people - Recall')
  })
  await capture('signup-closed')

  await step('the refused person has no session', async () => {
    expect((await page.request.get('/bff/user', { headers: { 'X-CSRF': '1' } })).status()).toBe(204)
  })
}
