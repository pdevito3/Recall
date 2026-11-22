import { expect, type Page } from '@playwright/test'
import { personalAccessTokensPaths } from '@tests/support/api/personal-access-tokens-api'
import { personalAccessTokens } from './personal-access-tokens.locators'

const answered = (page: Page, method: string, path: string) =>
  page.waitForResponse((r) => r.request().method() === method && new URL(r.url()).pathname === path)

export function personalAccessTokensPage(page: Page) {
  const at = personalAccessTokens(page)

  return {
    at,

    async open() {
      const listed = answered(page, 'GET', personalAccessTokensPaths.mine)
      await page.goto('/settings/personal-access-tokens')
      await expect(at.heading).toBeVisible()
      await listed
    },

    async openAt(instant: Date) {
      await page.clock.install({ time: instant })
      await this.open()
    },

    async reload() {
      const listed = answered(page, 'GET', personalAccessTokensPaths.mine)
      await page.reload()
      await expect(at.heading).toBeVisible()
      await listed
    },

    async openUser(userId: string, fullName: string) {
      const listed = answered(page, 'GET', personalAccessTokensPaths.ofUser(userId))
      await page.goto(`/users/${userId}`)
      await expect(at.userHeading(fullName)).toBeVisible()
      await listed
    },

    async openForm() {
      await at.newToken.click()
      await expect(at.nameField).toBeVisible()
    },

    async pickExpiry(label: string | RegExp) {
      await at.expiry.click()
      await at.expiryOption(label).click()
    },

    async typeLastDay(digits: string) {
      await at.lastDayMonth.click()
      await page.keyboard.type(digits)
    },

    async create() {
      const created = answered(page, 'POST', personalAccessTokensPaths.mine)
      await at.create.click()
      await created
    },

    async revoke(id: string, name: string) {
      await at.revoke(name).click()
      const revoked = answered(page, 'POST', personalAccessTokensPaths.revoke(id))
      await at.confirmRevoke.click()
      await revoked
    },

    /** History /about, /settings, then this page, in one document, so that Back stays in the app. */
    /** Presses Back in the task of the click on Create, before React renders the pending create, as a quick person can on a slow phone. */
    createAndGoBackAtOnce: () =>
      at.create.evaluate((button: HTMLElement) => {
        button.click()
        history.back()
      }),

    async openAfterAboutAndSettings() {
      await page.goto('/about')
      await at.settingsNavLink.click()
      await at.settingsLink.click()
      await expect(at.heading).toBeVisible()
    },

    async createNamed(name: string) {
      await this.openForm()
      await at.nameField.fill(name)
      await this.create()
    },

    warnsAtAClose: () =>
      page.evaluate(() => {
        const close = new Event('beforeunload', { cancelable: true })
        window.dispatchEvent(close)
        return close.defaultPrevented
      }),

    signOutInAnotherTab: () => page.evaluate(() => new BroadcastChannel('recall.session').postMessage('signed-out')),

    /** Counts the prompts of the browser, and answers each with Cancel, as a person who stays does. */
    countThePrompts() {
      let prompts = 0
      page.on('dialog', (dialog) => {
        prompts += 1
        void dialog.dismiss()
      })
      return () => prompts
    },

    clipboardText: () => page.evaluate(() => navigator.clipboard.readText()),

    storedText: () => page.evaluate(() => JSON.stringify({ ...localStorage }) + JSON.stringify({ ...sessionStorage })),

    pageOverflow: () => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth),
  }
}
