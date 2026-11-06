import { authApi, authPaths } from '@tests/support/api/auth-api'
import { as } from '@tests/support/auth/actors'
import { usersPaths } from '@tests/support/api/users-api'
import { aProblem } from '@tests/support/builders/problem-details-builder'
import { expect, test } from '@tests/support/fixtures'
import { blankPage, sessionPage } from './session.tasks'

test.describe('a visitor with no session', () => {
  test.use({ actor: as.anonymous() })

  test('goes to the login at once, with the deep link, its query, and its hash as the return URL', async ({ page, api }) => {
    api.mock(authApi.loginOpens())

    const { rendered } = await sessionPage(page).openAndGoToLogin('/about?tab=team&x=1#people')

    expect(api.last('get', authPaths.login)?.query.get('returnUrl')).toBe('/about?tab=team&x=1#people')
    expect(rendered, 'the app rendered a page before it went to the login').toBe(false)
  })

  test('starts one navigation to the login, because a second one aborts the first', async ({ page, api }) => {
    api.mock(authApi.loginOpens())

    const navigations = await sessionPage(page).openAndCountLoginNavigations('/about')

    expect(navigations).toBe(1)
  })

  test('replaces the deep link in the history, so Back from the login opens the page before it', async ({ page, api }) => {
    api.mock(authApi.loginOpens())
    const session = sessionPage(page)
    await session.openBlankPage()

    await session.openDeepLinkAndWaitForLogin('/about')
    await page.goBack()

    expect(page.url()).toBe(blankPage)
  })
})

test.describe('a session that ends while the page is open', () => {
  test('goes to the login at the next call of the API, with the page as the return URL, and shows no toast', async ({ page, api }) => {
    api.mock(authApi.loginOpens())
    const session = sessionPage(page)
    await session.open('/about')

    api.mock(...authApi.sessionEnded(usersPaths.current))
    const { toasted } = await session.followAndGoToLogin(session.at.homeLink)

    expect(api.last('get', authPaths.login)?.query.get('returnUrl')).toBe('/')
    expect(toasted, 'a toast showed before the login').toBe(false)
  })
})

test.describe('a sign-out in another tab', () => {
  test('takes this tab to the signed-out page, with no action in this tab', async ({ page, context, api }) => {
    const session = sessionPage(page)
    await session.open('/')
    const otherPage = await context.newPage()
    const otherTab = sessionPage(otherPage)
    await otherTab.open('/about')

    api.mock(...authApi.logoutWorks(), { method: 'get', path: authPaths.me, status: 204 })
    await session.at.logout.click()

    await expect.poll(() => page.url()).toContain('signedOut=1')
    await expect.poll(() => otherPage.url()).toContain('signedOut=1')
    await expect(otherTab.at.navigation).toHaveCount(0)
  })

  test('leaves this tab as it is when the sign-out fails', async ({ page, context, api }) => {
    const session = sessionPage(page)
    await session.open('/')
    const otherPage = await context.newPage()
    const otherTab = sessionPage(otherPage)
    await otherTab.open('/about')

    api.mock(authApi.logoutFails())
    await session.signOutAndWaitForTheAnswer()

    await expect(otherTab.at.navigation).toBeVisible()
    expect(await otherTab.screenShowsOneColor()).toBe(false)
    expect(new URL(otherPage.url()).pathname).toBe('/about')
  })
})

test.describe('a sign-out with a call of the API that answers 401 after it', () => {
  test('opens the signed-out page, and starts no login with a return URL', async ({ page, api }) => {
    const session = sessionPage(page)
    await session.open('/about')

    api.mock(
      authApi.loginOpens(),
      { method: 'post', path: authPaths.logout, status: 204 },
      { method: 'get', path: authPaths.me, status: 204 },
    )
    await session.followAndSignOutBeforeA401(session.at.homeLink, usersPaths.current)

    await expect.poll(() => page.url(), { timeout: 10000 }).toContain('signedOut=1')
    expect(api.all('get', authPaths.login).filter((request) => request.query.has('returnUrl'))).toHaveLength(0)
  })

  test('goes to the logout of the provider when the provider has one, and starts no login', async ({ page, api }) => {
    const providerLogout = 'https://provider.test/logout'
    await page.route(`${providerLogout}*`, (route) => route.fulfill({ status: 200, contentType: 'text/html', body: 'provider' }))
    const session = sessionPage(page)
    await session.open('/about')

    api.mock(
      authApi.loginOpens(),
      { method: 'post', path: authPaths.logout, status: 200, body: { provider_logout_url: `${providerLogout}?client_id=recall` } },
      { method: 'get', path: authPaths.me, status: 204 },
    )
    await session.followAndSignOutBeforeA401(session.at.homeLink, usersPaths.current, '/logout')

    await expect.poll(() => page.url(), { timeout: 10000 }).toBe(`${providerLogout}?client_id=recall`)
    expect(api.all('get', authPaths.login)).toHaveLength(0)
  })
})

test.describe('a sign-out with a call of the API that answers 401 before its answer', () => {
  test('still goes to the logout of the provider, and starts no login', async ({ page, api }) => {
    const providerLogout = 'https://provider.test/logout'
    await page.route(`${providerLogout}*`, (route) => route.fulfill({ status: 200, contentType: 'text/html', body: 'provider' }))
    const session = sessionPage(page)
    await session.open('/about')

    api.mock(
      authApi.loginOpens(),
      { method: 'post', path: authPaths.logout, status: 200, body: { provider_logout_url: `${providerLogout}?client_id=recall` }, delayMs: 1500 },
      { method: 'get', path: authPaths.me, status: 204 },
    )
    await session.followAndSignOutWithA401BeforeItsAnswer(session.at.homeLink, usersPaths.current)

    await expect.poll(() => page.url(), { timeout: 10000 }).toBe(`${providerLogout}?client_id=recall`)
    expect(api.all('get', authPaths.login)).toHaveLength(0)
  })
})

test.describe('a call of the API that answers 401 while the session lives', () => {
  test('keeps the page, does not go to the login, and shows a toast', async ({ page, api }) => {
    const session = sessionPage(page)
    await session.open('/about')

    api.mock(...authApi.apiAnswers401WhileTheSessionLives(usersPaths.current))
    await session.followAndWaitForTheSessionCheck(session.at.homeLink)

    await expect(session.at.rejectedSignInToast).toBeVisible()
    await expect(session.at.navigation).toBeVisible()
    expect(api.all('get', authPaths.login)).toHaveLength(0)
  })
})

test.describe('a failed check of the session', () => {
  test('shows an alert and no page or toast, and does not go to the login', async ({ page, api }) => {
    api.mock(authApi.userFails(aProblem().saying('The session store is down.')))

    const session = sessionPage(page)
    await session.openWhileTheCheckFails('/about')

    await expect(session.at.navigation).toHaveCount(0)
    await expect(session.at.aboutHeading).toHaveCount(0)
    // The toast shows with the failed request, before the alert. A toast closes after 6 seconds, so read the count once.
    expect(await session.at.toasts.count()).toBe(0)
    expect(api.all('get', authPaths.login)).toHaveLength(0)
  })

  test('shows the alert when the check answers 401', async ({ page, api }) => {
    api.mock({ method: 'get', path: authPaths.me, status: 401 })

    const session = sessionPage(page)
    await session.openWhileTheCheckFails('/about')

    expect(api.all('get', authPaths.login)).toHaveLength(0)
  })

  test('shows the page after Try Again', async ({ page, api }) => {
    api.mock(authApi.userFails(aProblem().saying('The session store is down.')))
    const session = sessionPage(page)
    await session.openWhileTheCheckFails('/about')

    api.mock(authApi.userIs(as.user()))
    await session.tryAgain()

    await expect(session.at.aboutHeading).toBeVisible()
    await expect(session.at.checkFailed).toHaveCount(0)
  })

  test('shows Try Again as pending while the check runs again, and sends one check for two presses', async ({ page, api }) => {
    api.mock(authApi.userFails(aProblem().saying('The session store is down.')))
    const session = sessionPage(page)
    await session.openWhileTheCheckFails('/about')
    const checksBefore = api.all('get', authPaths.me).length

    api.mock({ ...authApi.userIs(as.user()), delayMs: 1000 })
    await session.pressTryAgain()
    await expect(session.at.tryAgain).toBeDisabled()
    await session.pressTryAgain()

    await expect(session.at.aboutHeading).toBeVisible()
    expect(api.all('get', authPaths.me)).toHaveLength(checksBefore + 1)
  })
})
