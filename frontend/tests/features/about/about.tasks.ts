import { expect, type Download, type Page } from '@playwright/test'
import { about } from './about.locators'

export function aboutPage(page: Page) {
  const at = about(page)

  return {
    at,

    async open() {
      await page.goto('/about')
      await expect(at.text).toBeVisible()
    },

    async downloadSupportBundle(): Promise<Download> {
      const download = page.waitForEvent('download')
      await at.supportBundleButton.click()
      return download
    },
  }
}
