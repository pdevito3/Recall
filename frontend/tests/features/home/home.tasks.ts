import { expect, type Locator, type Page } from '@playwright/test'
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

    async isAbove(upper: Locator, lower: Locator) {
      const [first, second] = await Promise.all([upper.boundingBox(), lower.boundingBox()])
      return first !== null && second !== null && first.y + first.height <= second.y
    },

    /** How far the page scrolls sideways. */
    sideScroll() {
      return page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)
    },

    async logOutFromTheUserMenu() {
      await at.userMenuLogout.click()
      await page.waitForURL('**/auth/login?signedOut=1')
    },

    /** Each word of the message that the browser breaks over two lines. */
    wordsBrokenInTheLogoutMessage() {
      return at.logoutFailed.evaluate((element) => {
        const text = element.firstChild as Text
        return [...text.data.matchAll(/\S+/g)]
          .filter((word) => {
            const range = document.createRange()
            range.setStart(text, word.index)
            range.setEnd(text, word.index + word[0].length)
            return new Set([...range.getClientRects()].map(({ top }) => top)).size > 1
          })
          .map(([word]) => word)
      })
    },
  }
}
