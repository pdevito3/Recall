import { expect, type Page } from '@playwright/test'
import { settingsPaths } from '@tests/support/api/settings-api'
import { settings } from './settings.locators'

export function settingsPage(page: Page) {
  const at = settings(page)

  return {
    at,

    sideScrollRange: () => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth),

    async open() {
      await page.goto('/settings')
      await expect(at.heading).toBeVisible()
    },

    async zoneNamesInOrder() {
      await at.zone.press('ArrowDown')
      await expect(at.zoneOption(/^Automatic/)).toBeVisible()
      return at.zoneOptions.allTextContents()
    },

    async pickZone(search: string, option: RegExp) {
      const saved = page.waitForResponse((r) => r.request().method() === 'PUT' && new URL(r.url()).pathname === settingsPaths.mine)
      await at.zone.fill(search)
      await at.zoneOption(option).click()
      await saved
    },

    async turnOnLocation() {
      const saved = page.waitForResponse((r) => r.request().method() === 'PUT' && new URL(r.url()).pathname === settingsPaths.mine)
      await at.location.click({ force: true })
      await saved
    },
  }
}
