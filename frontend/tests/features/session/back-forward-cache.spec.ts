import { authApi, authPaths } from '@tests/support/api/auth-api'
import { aProblem } from '@tests/support/builders/problem-details-builder'
import { expect, test } from '@tests/support/fixtures'
import { sessionPage } from './session.tasks'

// Playwright turns this cache off, and only the full Chromium build has it on in a headless run.
// The mocks of this spec send no no-store, so Chromium restores the page here. The real Server sends it, and Chromium then loads the page again. docs/authentication.md has the runs.
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

    await expect(session.at.navigation).toBeVisible()
    await expect.poll(() => session.screenShowsOneColor()).toBe(false)
    await session.at.userMenu.click()
    await session.at.logout.click({ trial: true })
    expect(await session.isStillTheFirstLoad()).toBe(true)
    expect(api.all('get', authPaths.me)).toHaveLength(checksBefore + 1)
  })

  test('loads again when another session lives now, so the next person does not see the words of the first', async ({ page, api }) => {
    const session = sessionPage(page)
    await session.leavePrivatePageForAnotherDocument()

    const nextPerson = { sub: '2', name: 'Next Person', sid: 'session-of-next-person', client_kind: 'web' }
    api.mock(authApi.loginOpens(), { method: 'get', path: authPaths.me, status: 200, body: nextPerson, delayMs: 500 })
    await session.goBackToTheCachedPage()

    await expect.poll(() => session.isStillTheFirstLoad()).toBe(false)
  })

  test('loads again and shows the alert when the check fails, with no private text', async ({ page, api }) => {
    const session = sessionPage(page)
    await session.leavePrivatePageForAnotherDocument()

    api.mock({ ...authApi.userFails(aProblem().saying('The session store is down.')), delayMs: 500 })
    await session.goBackToTheCachedPage()

    await expect(session.at.checkFailed).toBeVisible({ timeout: 10000 })
    await expect(session.at.navigation).toHaveCount(0)
  })

  test('loads again when the check does not answer in time, so a person does not wait on an empty page', async ({ page, api, actor }) => {
    const session = sessionPage(page)
    await session.leavePrivatePageForAnotherDocument()
    const checksBefore = api.all('get', authPaths.me).length

    api.mock({ ...authApi.userIs(actor), neverAnswers: true })
    await session.goBackToTheCachedPage()
    await expect.poll(() => api.all('get', authPaths.me).length).toBe(checksBefore + 1)
    api.mock(authApi.userIs(actor))

    await expect.poll(() => session.isStillTheFirstLoad(), { timeout: 15000 }).toBe(false)
  })
})
