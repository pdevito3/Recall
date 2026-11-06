import { expect, type Page } from '@playwright/test'
import { settingsPaths } from '@tests/support/api/settings-api'
import { settings } from './settings.locators'

export function settingsPage(page: Page) {
  const at = settings(page)

  return {
    at,

    async open() {
      await page.goto('/settings')
      await expect(at.heading).toBeVisible()
    },

    async turnOnLocation() {
      const saved = page.waitForResponse((r) => r.request().method() === 'PUT' && new URL(r.url()).pathname === settingsPaths.mine)
      await at.location.click({ force: true })
      await saved
    },
  }
}
