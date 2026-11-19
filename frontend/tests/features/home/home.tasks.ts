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

    async openWithNoEntries() {
      await this.open()
      await expect(at.emptyTimeline).toBeVisible()
    },

    async openWhileTheEntriesLoad() {
      await this.open()
      await expect(at.timelineLoading).toBeVisible()
    },

    /** The functional suite runs on localhost, which is a secure context, so a test sets the value of a LAN address. */
    async openOnPlainHttp() {
      await page.addInitScript(() => Object.defineProperty(window, 'isSecureContext', { value: false }))
      await this.open()
    },

    async record() {
      await at.record.click()
      await expect(at.newParagraph).toBeVisible()
    },

    async markAParagraph() {
      await at.newParagraph.click()
      await expect(at.paragraphMarks).toBeVisible()
    },

    /** Each element of the mic card that shows out of the card on the left or the right, by how many px. */
    partsOutOfTheMicCard() {
      return at.micCard.evaluate((card) => {
        const frame = card.getBoundingClientRect()
        const shownSpan = (element: Element) => {
          let { left, right } = element.getBoundingClientRect()
          for (let ancestor = element.parentElement; ancestor && ancestor !== card; ancestor = ancestor.parentElement) {
            if (getComputedStyle(ancestor).overflowX === 'visible') continue
            const clip = ancestor.getBoundingClientRect()
            left = Math.max(left, clip.left)
            right = Math.min(right, clip.right)
          }
          return { left, right }
        }
        return [...card.querySelectorAll('*')].flatMap((element) => {
          const { left, right } = shownSpan(element)
          const out = Math.round(Math.max(frame.left - left, right - frame.right))
          return right > left && out > 0 ? [`${element.tagName.toLowerCase()} "${element.textContent?.slice(0, 20)}" by ${out} px`] : []
        })
      })
    },

    async offCenter(element: Locator) {
      const [card, box] = await Promise.all([at.micCard.boundingBox(), element.boundingBox()])
      return Math.round(box!.x + box!.width / 2 - (card!.x + card!.width / 2))
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
