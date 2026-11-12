import { expect, type Locator, type Page } from '@playwright/test'
import { settingsPaths } from '@tests/support/api/settings-api'
import { settings } from './settings.locators'

export function settingsPage(page: Page) {
  const at = settings(page)

  return {
    at,

    async isInsideTheWindow(element: Locator) {
      const box = await element.boundingBox()
      const width = page.viewportSize()?.width ?? 0
      return box !== null && box.x >= 0 && box.x + box.width <= width
    },

    /** As a browser in a zone that its tzdata does not have: Intl names no zone, which an explicit zone still has. */
    hideTheZoneOfTheBrowser: () =>
      page.addInitScript(() => {
        const hideZone = (format: Intl.DateTimeFormat, options?: Intl.DateTimeFormatOptions) => {
          if (options?.timeZone) return format
          const resolved = format.resolvedOptions.bind(format)
          format.resolvedOptions = () => ({ ...resolved(), timeZone: undefined as unknown as string })
          return format
        }
        Intl.DateTimeFormat = new Proxy(Intl.DateTimeFormat, {
          construct: (Real, [locales, options]: [string | undefined, Intl.DateTimeFormatOptions | undefined]) => hideZone(new Real(locales, options), options),
          apply: (Real, _, [locales, options]: [string | undefined, Intl.DateTimeFormatOptions | undefined]) => hideZone(new Real(locales, options), options),
        })
      }),

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
