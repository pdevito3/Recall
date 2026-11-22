import { randomUUID } from 'node:crypto'
import { test, expect, type Page } from '@playwright/test'
import { loadIdp } from './idps.ts'

/**
 * Two people on one browser, with no route mocks. All tabs share one session cookie, so the second sign-in changes
 * the person of the first tab under its page. The stack needs Auth__OpenSignup=true, so that the second person has a
 * journal to read. See docs/authentication.md, "One browser, two people".
 *
 * Run with: pnpm test:e2e:stack two-people
 */

const currentIdp = loadIdp(process.env.IDP)
const firstPerson = currentIdp.email ?? ''
const secondPerson = currentIdp.secondPersonEmail ?? ''

const appOrigin = process.env.BASE_URL ?? 'http://localhost:24710'

const composer = (page: Page) => page.getByRole('textbox', { name: 'New entry' })

async function signInAtIdp(page: Page, email: string) {
  await page.locator(currentIdp.usernameSelector).fill(email)
  await page.locator(currentIdp.submitSelector).click()
  await page.waitForURL((url) => url.origin === appOrigin && !url.pathname.startsWith('/auth'), { timeout: 30_000 })
  await expect(page.getByRole('navigation', { name: 'Main' })).toBeVisible({ timeout: 30_000 })
}

/**
 * Ends the session here and at the provider, so that the next person signs in with their own name. The provider ends
 * its session and returns, so the signed-out page has no `Sign out of` link.
 */
async function signOutHereAndAtIdp(page: Page) {
  await page.getByRole('button', { name: /^User menu for/ }).click()
  await page.getByRole('menuitem', { name: 'Logout' }).click()
  await page.waitForURL(/signedOut=1/, { timeout: 30_000 })
  await expect(page.getByRole('heading', { name: 'You are signed out' })).toBeVisible()
  await expect(page.getByRole('link', { name: /^Sign out of/ })).toHaveCount(0)
}

/**
 * Ends the session here and at the provider with no logout of a page, as when the session expires or is revoked after the
 * provider session ends. So the other tabs hear nothing and keep their page, and the next person who signs in on this browser sends the cookie under
 * them. A logout from the user menu would tell each tab to leave.
 */
async function endSessionWithNoTabTold(page: Page) {
  const logout = await page.request.post('/auth/logout', { headers: { 'X-CSRF': '1' } })
  expect(logout.status()).toBe(200)
  const { provider_logout_url } = (await logout.json()) as { provider_logout_url: string }
  await page.goto(provider_logout_url)
  await page.waitForURL(/signedOut=1/, { timeout: 30_000 })
  await expect(page.getByRole('link', { name: /^Sign out of/ })).toHaveCount(0)
}

async function openSignInForm(page: Page) {
  await page.goto('/auth/login?signedOut=1')
  await page.getByRole('link', { name: /^Sign in with/ }).click()
}

/** A request from the page, with the name of the person of the page, as the API client sends it. */
async function askAs(page: Page, sub: string, path: string) {
  const response = await page.request.get(path, { headers: { 'X-CSRF': '1', 'X-Recall-User': sub } })
  return { status: response.status(), body: await response.text() }
}

const subOf = (page: Page) =>
  page.evaluate(async () => {
    const response = await fetch('/auth/me', { headers: { 'X-CSRF': '1' } })
    return ((await response.json()) as { sub: string }).sub
  })

const outboxWords = (page: Page) =>
  page.evaluate(() =>
    Object.keys(localStorage)
      .filter((key) => key.startsWith('recall.unsent.'))
      .map((key) => ({ key, stored: localStorage.getItem(key) ?? '' })),
  )

test('the words of a tab go to the journal of the person who wrote them, after another person signs in on the browser', async ({ context, page: firstTab }) => {
  test.skip(!secondPerson, `The IdP file of ${currentIdp.name} names no second person.`)
  test.setTimeout(180_000)
  const earlierWords = `Earlier words ${randomUUID()}`
  const laterWords = `Later words ${randomUUID()}`

  await firstTab.goto('/')
  await signInAtIdp(firstTab, firstPerson)
  const firstSub = await subOf(firstTab)
  await composer(firstTab).click()
  await firstTab.keyboard.type(earlierWords)
  await expect(firstTab.getByRole('status').filter({ hasText: /^Saved at/ }).first()).toBeVisible({ timeout: 15_000 })

  // The save of the later words is on its way when the next person signs in, so only the outbox holds them
  const draftSave = '**/api/v1/sessions/*/draft'
  const heldSave = firstTab.waitForRequest((request) => request.method() === 'PUT' && request.url().endsWith('/draft'))
  await firstTab.route(draftSave, () => undefined)
  await firstTab.keyboard.type(` ${laterWords}`)
  await heldSave

  const secondTab = await context.newPage()
  await endSessionWithNoTabTold(secondTab)
  await openSignInForm(secondTab)
  await signInAtIdp(secondTab, secondPerson)
  const secondSub = await subOf(secondTab)
  expect(secondSub).not.toBe(firstSub)

  await firstTab.waitForURL(/\/auth\/login\?anotherPerson=1/, { timeout: 30_000 })
  await expect(firstTab.locator('body')).toContainText('Another person signed in on this browser')
  await expect(firstTab.locator('body')).not.toContainText('Earlier words')
  const held = await outboxWords(firstTab)
  expect(held.filter(({ key }) => key.includes(`.${firstSub}.`)).map(({ stored }) => stored).join()).toContain(laterWords)
  expect(held.filter(({ key }) => key.includes(`.${secondSub}.`))).toEqual([])

  const refused = await askAs(firstTab, firstSub, '/api/v1/sessions')
  expect(refused.status).toBe(409)
  expect(refused.body).toContain('urn:recall:problem:page-user-mismatch')
  expect(refused.body).not.toContain('Earlier words')

  await secondTab.bringToFront()
  const journalOfSecond = await askAs(secondTab, secondSub, '/api/v1/sessions')
  expect(journalOfSecond.status).toBe(200)
  expect(journalOfSecond.body).not.toContain(earlierWords)
  expect(journalOfSecond.body).not.toContain(laterWords)
  await secondTab.goto('/')
  await expect(secondTab.getByRole('navigation', { name: 'Main' })).toBeVisible()
  await expect(secondTab.locator('body')).not.toContainText('Earlier words')

  await signOutHereAndAtIdp(secondTab)
  await secondTab.close()

  await firstTab.unroute(draftSave)
  await firstTab.bringToFront()
  await firstTab.getByRole('link', { name: /^Sign in with/ }).click()
  await signInAtIdp(firstTab, firstPerson)

  await expect.poll(async () => (await askAs(firstTab, firstSub, '/api/v1/sessions')).body, { timeout: 30_000 }).toContain(laterWords)
  await expect.poll(async () => outboxWords(firstTab), { timeout: 30_000 }).toEqual([])
})
