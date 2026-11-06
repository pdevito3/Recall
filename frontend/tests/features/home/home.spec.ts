import { authApi, authPaths } from '@tests/support/api/auth-api'
import { usersApi } from '@tests/support/api/users-api'
import { as } from '@tests/support/auth/actors'
import { aProblem } from '@tests/support/builders/problem-details-builder'
import { expect, test } from '@tests/support/fixtures'
import { homePage } from './home.tasks'

test.describe('a signed-in user on the home page', () => {
  test.use({ actor: as.user('Bob Smith') })

  test('sees the name that the Server reads from the access token', async ({ page, api }) => {
    api.mock(usersApi.currentIs({ identifier: '1', name: 'Robert Smith' }))
    const home = homePage(page)

    await home.open()

    await expect(home.at.signedInAs('Robert Smith')).toBeVisible()
  })

  test('sees the identifier when the token has no name', async ({ page, api }) => {
    api.mock(usersApi.currentIs({ identifier: 'user-1', name: null }))
    const home = homePage(page)

    await home.open()

    await expect(home.at.signedInAs('user-1')).toBeVisible()
  })

  test('sees an error when the Server fails to load the user', async ({ page, api }) => {
    api.mock(usersApi.currentFails(aProblem().saying('The Server is down.')))
    const home = homePage(page)

    await home.open()

    await expect(home.at.userFailed).toBeVisible()
  })

  test('ends the session with the CSRF header, then goes to the signed-out page', async ({ page, api }) => {
    api.mock(usersApi.currentIs({ identifier: '1', name: 'Robert Smith' }), ...authApi.logoutWorks())
    const home = homePage(page)
    await home.open()

    await home.logOut()

    expect(api.last('post', authPaths.logout)?.headers.get('x-csrf')).toBe('1')
  })

  test('stays on the page and says that the person is still signed in when the logout fails', async ({ page, api }) => {
    api.mock(usersApi.currentIs({ identifier: '1', name: 'Robert Smith' }), authApi.logoutFails())
    const home = homePage(page)
    await home.open()

    await home.at.logout.click()

    await expect(home.at.logoutFailed).toBeVisible()
    expect(new URL(page.url()).pathname).toBe('/')
    expect(api.all('get', authPaths.login)).toHaveLength(0)
  })

  test('shows the name of the session in the user menu, and logs out from it', async ({ page, api }) => {
    api.mock(usersApi.currentIs({ identifier: '1', name: 'Robert Smith' }), ...authApi.logoutWorks())
    const home = homePage(page)
    await home.open()

    await home.at.userMenu('Bob Smith').click()

    await expect(home.at.userMenuName('Bob Smith')).toBeVisible()
    await home.logOutFromTheUserMenu()
    expect(api.last('post', authPaths.logout)?.headers.get('x-csrf')).toBe('1')
  })
})
