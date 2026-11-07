import { authApi, authPaths } from '@tests/support/api/auth-api'
import { usersApi } from '@tests/support/api/users-api'
import { as } from '@tests/support/auth/actors'
import { expect, test } from '@tests/support/fixtures'
import { appShellPage } from '../app-shell/app-shell.tasks'
import { homePage } from './home.tasks'

test.describe('the mic message on the journal page', () => {
  test.use({ actor: as.user('Bob Smith') })

  test('sees that the mic stays off on plain HTTP, with a link to the HTTPS section', async ({ page, api }) => {
    api.mock(usersApi.currentIs({ identifier: '1', name: 'Robert Smith' }))
    const home = homePage(page)

    await home.openOnPlainHttp()

    await expect(home.at.micMessage).toBeVisible()
    await expect(home.at.httpsGuide).toBeVisible()
    await expect(home.at.journalComposer).toBeVisible()
  })

  test('sees no mic message on HTTPS', async ({ page, api }) => {
    api.mock(usersApi.currentIs({ identifier: '1', name: 'Robert Smith' }))
    const home = homePage(page)

    await home.open()

    await expect(home.at.journalComposer).toBeVisible()
    await expect(home.at.micMessage).toHaveCount(0)
  })
})

test.describe('the user menu of a signed-in user', () => {
  test.use({ actor: as.user('Bob Smith') })

  test('ends the session with the CSRF header, then goes to the signed-out page', async ({ page, api }) => {
    api.mock(usersApi.currentIs({ identifier: '1', name: 'Robert Smith' }), ...authApi.logoutWorks())
    const home = homePage(page)
    await home.open()

    await home.at.userMenu('Bob Smith').click()
    await home.logOutFromTheUserMenu()

    expect(api.last('post', authPaths.logout)?.headers.get('x-csrf')).toBe('1')
  })

  test('stays on the page and says that the person is still signed in when the logout fails', async ({ page, api }) => {
    api.mock(usersApi.currentIs({ identifier: '1', name: 'Robert Smith' }), authApi.logoutFails())
    const home = homePage(page)
    await home.open()

    await home.at.userMenu('Bob Smith').click()
    await home.at.userMenuLogout.click()

    await expect(home.at.logoutFailed).toBeVisible()
    expect(new URL(page.url()).pathname).toBe('/')
    expect(api.all('get', authPaths.login)).toHaveLength(0)
  })

  test('shows the name of the session in the user menu', async ({ page, api }) => {
    api.mock(usersApi.currentIs({ identifier: '1', name: 'Robert Smith' }))
    const home = homePage(page)
    await home.open()

    await home.at.userMenu('Bob Smith').click()

    await expect(home.at.userMenuName('Bob Smith')).toBeVisible()
  })
})

test.describe('a logout that fails on a phone', () => {
  test.use({ actor: as.user('Bob Smith'), viewport: { width: 390, height: 844 } })

  test('leaves the user menu free, so that the person can try again, and breaks no word of its message', async ({ page, api }) => {
    api.mock(usersApi.currentIs({ identifier: '1', name: 'Robert Smith' }), authApi.logoutFails())
    const home = homePage(page)
    await home.open()
    await appShellPage(page).openMobileSidebar()
    await home.at.userMenu('Bob Smith').click()

    await home.at.userMenuLogout.click()

    await expect(home.at.logoutFailed).toBeVisible()
    expect(await home.wordsBrokenInTheLogoutMessage()).toEqual([])
    // The sign-out closes the drawer, so that its toast shows. A toast over the button would take this click, and it closes only after 6 seconds.
    await appShellPage(page).openMobileSidebar()
    await home.at.userMenu('Bob Smith').click({ timeout: 2000 })
    await expect(home.at.userMenuLogout).toBeVisible()
  })
})
