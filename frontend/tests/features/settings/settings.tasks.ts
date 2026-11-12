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

    /**
     * Intl names this zone of the system, as a context zone cannot change in a test. Null is a zone that the tzdata of the
     * browser does not have, so Intl names no zone.
     */
    async nameTheZoneOfTheSystem(zone: string | null) {
      await page.addInitScript((first) => {
        const system = window as unknown as { zoneOfTheSystem: string | null }
        system.zoneOfTheSystem = first
        const nameZone = (format: Intl.DateTimeFormat, options?: Intl.DateTimeFormatOptions) => {
          if (options?.timeZone) return format
          const resolved = format.resolvedOptions.bind(format)
          format.resolvedOptions = () => ({ ...resolved(), timeZone: system.zoneOfTheSystem as string })
          return format
        }
        Intl.DateTimeFormat = new Proxy(Intl.DateTimeFormat, {
          construct: (Real, [locales, options]: [string | undefined, Intl.DateTimeFormatOptions | undefined]) => nameZone(new Real(locales, options), options),
          apply: (Real, _, [locales, options]: [string | undefined, Intl.DateTimeFormatOptions | undefined]) => nameZone(new Real(locales, options), options),
        })
      }, zone)
      return {
        travelTo: (next: string) =>
          page.evaluate((nextZone) => {
            ;(window as unknown as { zoneOfTheSystem: string }).zoneOfTheSystem = nextZone
            document.dispatchEvent(new Event('visibilitychange'))
            window.dispatchEvent(new Event('focus'))
          }, next),
      }
    },

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

    aReportReply() {
      let arrived = false
      const reply = page.waitForResponse((r) => new URL(r.url()).pathname === settingsPaths.deviceTimeZone).then(() => {
        arrived = true
      })
      return { arrived: () => arrived, reply }
    },

    async turnOnLocation() {
      const saved = page.waitForResponse((r) => r.request().method() === 'PUT' && new URL(r.url()).pathname === settingsPaths.mine)
      await at.location.click({ force: true })
      await saved
    },
  }
}
