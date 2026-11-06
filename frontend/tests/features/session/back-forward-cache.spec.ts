import { authApi, authPaths } from '@tests/support/api/auth-api'
import { aProblem } from '@tests/support/builders/problem-details-builder'
import { expect, test } from '@tests/support/fixtures'
import { sessionPage } from './session.tasks'

// Playwright turns this cache off, and only the full Chromium build has it on in a headless run.
test.use({ channel: 'chromium', launchOptions: { ignoreDefaultArgs: ['--disable-back-forward-cache'] } })

test.describe('a page that Back restores from the back-forward cache', () => {
  test('shows nothing while it checks the session, then opens the signed-out page when the session ended', async ({ page, api }) => {
    const session = sessionPage(page)
    await session.leavePrivatePageForAnotherDocument()

    api.mock(authApi.loginOpens(), { method: 'get', path: authPaths.me, status: 204, delayMs: 1500 })
    await session.goBackToTheCachedPage()

    expect(await session.screenShowsOneColor(), 'the screen while the check runs').toBe(true)
    await expect.poll(() => page.url()).toContain('/auth/login?signedOut=1')
  })

  test('shows the same page again when the session lives, with no new load', async ({ page, api }) => {
    const session = sessionPage(page)
    await session.leavePrivatePageForAnotherDocument()

    const checksBefore = api.all('get', authPaths.me).length
    await session.goBackToTheCachedPage()

    await expect(session.at.signedInAs).toBeVisible()
    await expect.poll(() => session.screenShowsOneColor()).toBe(false)
    expect(await session.isStillTheFirstLoad()).toBe(true)
    expect(api.all('get', authPaths.me)).toHaveLength(checksBefore + 1)
  })

  test('loads again when another session lives now, so the next person does not see the words of the first', async ({ page, api }) => {
    const session = sessionPage(page)
    await session.leavePrivatePageForAnotherDocument()

    const nextPerson = { sub: '2', name: 'Next Person', sid: 'session-of-next-person', client_kind: 'web' }
    api.mock({ method: 'get', path: authPaths.me, status: 200, body: nextPerson, delayMs: 500 })
    await session.goBackToTheCachedPage()

    await expect.poll(() => session.isStillTheFirstLoad()).toBe(false)
  })

  test('loads again and shows the alert when the check fails, with no private text', async ({ page, api }) => {
    const session = sessionPage(page)
    await session.leavePrivatePageForAnotherDocument()

    api.mock({ ...authApi.userFails(aProblem().saying('The session store is down.')), delayMs: 500 })
    await session.goBackToTheCachedPage()

    await expect(session.at.checkFailed).toBeVisible({ timeout: 10000 })
    await expect(session.at.signedInAs).toHaveCount(0)
  })

  test('loads again when the check does not answer in time, so a person does not wait on an empty page', async ({ page }) => {
    const session = sessionPage(page)
    await session.leavePrivatePageForAnotherDocument()

    await page.route(`**${authPaths.me}`, () => {}, { times: 1 })
    await session.goBackToTheCachedPage()

    await expect.poll(() => session.isStillTheFirstLoad(), { timeout: 4500 }).toBe(false)
  })
})
