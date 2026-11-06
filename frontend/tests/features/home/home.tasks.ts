import { expect, type Page } from '@playwright/test'
import { home } from './home.locators'

export function homePage(page: Page) {
  const at = home(page)

  return {
    at,

    async open() {
      await page.goto('/')
      await expect(at.heading).toBeVisible()
    },

    /** The functional suite runs on localhost, which is a secure context, so a test sets the value of a LAN address. */
    async openOnPlainHttp() {
      await page.addInitScript(() => Object.defineProperty(window, 'isSecureContext', { value: false }))
      await this.open()
    },

    async logOutFromTheUserMenu() {
      await at.userMenuLogout.click()
      await page.waitForURL('**/auth/login?signedOut=1')
    },
  }
}
