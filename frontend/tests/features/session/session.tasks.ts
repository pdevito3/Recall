import { expect, type Locator, type Page } from '@playwright/test'
import { authPaths } from '@tests/support/api/auth-api'
import { session } from './session.locators'

export const blankPage = 'data:text/html,blank'

export function sessionPage(page: Page) {
  const at = session(page)
  const answered = (path: string) =>
    page.waitForResponse((r) => r.request().method() === 'GET' && new URL(r.url()).pathname === path)

  return {
    at,

    /** The page is gone when the login opens, so a script in the page reports each render at once. */
    async openAndGoToLogin(path: string) {
      let rendered = false
      await page.exposeFunction('reportRender', () => {
        rendered = true
      })
      await page.addInitScript(() => {
        const report = (window as unknown as { reportRender: () => void }).reportRender
        new MutationObserver(() => {
          if (document.getElementById('root')?.hasChildNodes()) report()
        }).observe(document, { childList: true, subtree: true })
      })

      const login = answered(authPaths.login)
      // The SPA leaves the page as it loads, so the wait is for the commit and not for the load.
      await page.goto(path, { waitUntil: 'commit' })
      await login
      return { rendered }
    },

    async openAndCountLoginNavigations(path: string) {
      let navigations = 0
      await page.exposeFunction('reportLoginNavigation', () => {
        navigations += 1
      })
      await page.addInitScript((loginPath) => {
        const { navigation, reportLoginNavigation } = window as unknown as {
          navigation: EventTarget
          reportLoginNavigation: () => void
        }
        navigation.addEventListener('navigate', (event) => {
          const { destination } = event as unknown as { destination: { url: string } }
          if (new URL(destination.url).pathname === loginPath) reportLoginNavigation()
        })
      }, authPaths.login)

      const login = answered(authPaths.login)
      await page.goto(path, { waitUntil: 'commit' })
      await login
      return navigations
    },

    async openBlankPage() {
      await page.goto(blankPage)
    },

    async openDeepLinkAndWaitForLogin(path: string) {
      await page.goto(path, { waitUntil: 'commit' })
      await expect.poll(() => new URL(page.url()).pathname).toBe(authPaths.login)
      await page.waitForLoadState()
    },

    async open(path: string) {
      await page.goto(path)
      await expect(at.navigation).toBeVisible()
    },

    /** The page is gone when the login opens, so a script in the page reports each toast at once. */
    async followAndGoToLogin(link: Locator) {
      let toasted = false
      await page.exposeFunction('reportToast', () => {
        toasted = true
      })
      await page.evaluate(() => {
        const report = (window as unknown as { reportToast: () => void }).reportToast
        new MutationObserver(() => {
          if (document.querySelector('[role="alertdialog"]')) report()
        }).observe(document.body, { childList: true, subtree: true })
      })

      const login = answered(authPaths.login)
      await link.click()
      await login
      return { toasted }
    },

    /** A 401 of the API makes the app check the session again. */
    async followAndWaitForTheSessionCheck(link: Locator) {
      const checked = answered(authPaths.me)
      await link.click()
      await checked
    },

    async followAndSignOutBeforeA401(link: Locator, apiPath: string) {
      let answerThe401!: () => void
      const logoutAnswered = new Promise<void>((resolve) => {
        answerThe401 = resolve
      })
      const logoutOpensThe401 = page
        .waitForResponse((r) => r.request().method() === 'POST' && new URL(r.url()).pathname === authPaths.logout)
        .then(answerThe401)
      const checked = answered(authPaths.me)
      await page.route((url) => url.pathname === apiPath, async (route) => {
        await logoutAnswered
        await route.fulfill({ status: 401 })
      })
      await page.route((url) => url.pathname === authPaths.login, async (route) => {
        await checked
        await route.fallback()
      })

      await link.click()
      await at.userMenu.click()
      // A fast logout starts its navigation inside the click, and then the click waits for the login that this check holds.
      await Promise.all([at.logout.click(), logoutOpensThe401, checked])
    },

    async openWhileTheCheckFails(path: string) {
      await page.goto(path)
      await expect(at.checkFailed).toBeVisible()
    },

    async pressTryAgain() {
      await at.tryAgain.focus()
      await page.keyboard.press('Enter')
    },

    async tryAgain() {
      const settled = answered(authPaths.me)
      await at.tryAgain.click()
      await settled
    },
  }
}
