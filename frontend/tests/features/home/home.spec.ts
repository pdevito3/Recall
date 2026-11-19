import { authApi, authPaths } from '@tests/support/api/auth-api'
import { sessionsApi } from '@tests/support/api/sessions-api'
import { usersApi } from '@tests/support/api/users-api'
import { as } from '@tests/support/auth/actors'
import { aSession } from '@tests/support/builders/session-builder'
import { expect, test } from '@tests/support/fixtures'
import { theWebFontsLoad, useDefaultFontSize } from '@tests/support/screen'
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
    await expect(home.at.record).toBeDisabled()
    await expect(home.at.journalComposer).toBeVisible()
  })

  test('sees no mic message on HTTPS', async ({ page, api }) => {
    api.mock(usersApi.currentIs({ identifier: '1', name: 'Robert Smith' }))
    const home = homePage(page)

    await home.open()

    await expect(home.at.journalComposer).toBeVisible()
    await expect(home.at.record).toBeEnabled()
    await expect(home.at.micMessage).toHaveCount(0)
  })
})

test.describe('the mic card on a phone', () => {
  test.use({ actor: as.user('Bob Smith'), viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true })

  test('is the first thing under the date, and the page open puts no keyboard over it', async ({ page, api }) => {
    api.mock(usersApi.currentIs({ identifier: '1', name: 'Robert Smith' }))
    const home = homePage(page)

    await home.open()

    await expect(home.at.record).toBeInViewport()
    await expect(home.at.journalComposer).toBeVisible()
    await expect(home.at.journalComposer).not.toBeFocused()
    expect(await home.isAbove(home.at.micCard, home.at.journalComposer)).toBe(true)
    expect(await home.sideScroll()).toBe(0)
  })

  test('takes a tap of 44 px on the link that changes the engine', async ({ page, api }) => {
    api.mock(usersApi.currentIs({ identifier: '1', name: 'Robert Smith' }))
    const home = homePage(page)

    await home.open()

    await expect(home.at.changeEngine).toBeVisible()
    await theWebFontsLoad(page)
    const box = await home.at.changeEngine.boundingBox()
    expect(Math.min(box!.width, box!.height)).toBeGreaterThanOrEqual(44)
  })
})

test.describe('the mic card with a default font of 32 px on a 320 px screen', () => {
  test.use({ viewport: { width: 320, height: 800 }, permissions: ['microphone'] })

  test('while it records, stays centered with nothing out of the card, and the page does not scroll sideways', async ({ page, api }) => {
    // The recorder sends a slice of audio each second, and the first slice makes an entry. The upload is not in this test.
    api.mock({ ...sessionsApi.creates(aSession().build()), neverAnswers: true })
    const home = homePage(page)
    await useDefaultFontSize(page, 32)
    await home.openWithNoEntries()

    await home.record()
    await home.markAParagraph()

    expect(await home.partsOutOfTheMicCard()).toEqual([])
    expect(await home.offCenter(home.at.newParagraph)).toBe(0)
    expect(await home.sideScroll()).toBe(0)
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
