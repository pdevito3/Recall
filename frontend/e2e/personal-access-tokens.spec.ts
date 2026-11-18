import { randomUUID } from 'node:crypto'
import { test, expect, type Page } from '@playwright/test'
import { loadIdp } from './idps.ts'

/**
 * A personal access token made in the SPA works as a bearer from outside the browser, and stops at the next request
 * after a revoke in the SPA. See docs/authentication.md, "Personal access tokens".
 *
 * Run with: pnpm test:e2e:stack personal-access-tokens
 */

const currentIdp = loadIdp(process.env.IDP)
const appOrigin = process.env.BASE_URL ?? 'http://localhost:24710'

async function signIn(page: Page) {
  await page.goto('/')
  await page.locator(currentIdp.usernameSelector).fill((currentIdp.email ?? currentIdp.username)!)
  if (currentIdp.passwordSelector) await page.locator(currentIdp.passwordSelector).fill(currentIdp.password!)
  await page.locator(currentIdp.submitSelector).click()
  await page.waitForURL((url) => url.origin === appOrigin && !url.pathname.startsWith('/auth'), { timeout: 30_000 })
  await expect(page.getByRole('navigation', { name: 'Main' })).toBeVisible({ timeout: 30_000 })
}

/** A call from a script, with no cookie and no X-CSRF header. */
const statusWithToken = async (value: string) =>
  (await fetch(new URL('/api/v1/sessions', appOrigin), { headers: { Authorization: `Bearer ${value}` } })).status

test('a token made in the SPA reads the API as its person until a revoke in the SPA', { tag: ['@PAT-1', '@PAT-9'] }, async ({ page }) => {
  test.skip(Boolean(currentIdp.twoStepLogin), `This test signs in with a one-step form, and ${currentIdp.name} has two steps.`)
  const name = `Stack ${randomUUID().slice(0, 8)}`
  await signIn(page)
  await page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Personal access tokens', exact: true }).click()
  await expect(page.getByRole('heading', { level: 1, name: 'Personal access tokens' })).toBeVisible()

  await page.getByRole('button', { name: 'New token', exact: true }).click()
  const dialog = page.getByRole('dialog')
  await dialog.getByRole('textbox', { name: 'Name' }).fill(name)
  await dialog.getByRole('checkbox', { name: 'Read my journal' }).click({ force: true })
  const created = page.waitForResponse((r) => r.request().method() === 'POST' && new URL(r.url()).pathname === '/api/v1/personal-access-tokens')
  await dialog.getByRole('button', { name: 'Create token', exact: true }).click()
  expect((await created).status()).toBe(201)
  const value = (await dialog.getByText(/^rcl_pat_/).textContent())!.trim()
  expect(value).toMatch(/^rcl_pat_[A-Za-z0-9_-]{43}$/)

  expect(await statusWithToken(value), 'the token before the revoke').toBe(200)

  await dialog.getByRole('button', { name: 'Done', exact: true }).click()
  await expect(page.getByText(/rcl_pat_/)).toHaveCount(0)
  const row = page.getByRole('list', { name: 'Your personal access tokens' }).getByRole('listitem', { name, exact: true })
  await expect(row).toContainText('Active')
  await page.getByRole('button', { name: `Revoke ${name}`, exact: true }).click()
  const revoked = page.waitForResponse((r) => r.request().method() === 'POST' && new URL(r.url()).pathname.endsWith('/revoke'))
  await page.getByRole('dialog').getByRole('button', { name: 'Revoke token', exact: true }).click()
  expect((await revoked).status()).toBe(204)
  await expect(row).toContainText('Revoked')

  expect(await statusWithToken(value), 'the token after the revoke').toBe(401)
})
