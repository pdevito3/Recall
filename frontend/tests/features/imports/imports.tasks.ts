import { expect, type Locator, type Page } from '@playwright/test'
import { importPaths } from '@tests/support/api/imports-api'
import { imports } from './imports.locators'

const answered = (page: Page, method: string, path: string) =>
  page.waitForResponse((r) => r.request().method() === method && new URL(r.url()).pathname === path)

export function importsPage(page: Page) {
  const at = imports(page)

  return {
    at,

    async openList() {
      await page.goto('/imports')
      await expect(at.heading).toBeVisible()
    },

    async openBatch(id: string, name: string, query = '') {
      await page.goto(`/imports/${id}${query}`)
      await expect(at.batchHeading(name)).toBeVisible()
    },

    async openReviewWithEntries(id: string, name: string) {
      await this.openBatch(id, name)
      await expect(at.counts).toBeVisible()
      await expect(at.loadingEntries).toHaveCount(0)
    },

    async openSettings() {
      await page.goto('/settings')
      await expect(page.getByRole('heading', { level: 1, name: 'Settings' })).toBeVisible()
    },

    async isInsideTheWindowWidth(element: Locator) {
      const box = await element.boundingBox()
      const width = page.viewportSize()?.width ?? 0
      return box !== null && box.x >= 0 && box.x + box.width <= width
    },

    sideScrollRange: () => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth),

    async reject(id: string, time: string) {
      const reviewed = answered(page, 'PUT', importPaths.entryReview(id))
      await at.reject(time).click()
      await reviewed
    },

    async rejectDay(id: string, heading: string) {
      const reviewed = answered(page, 'PUT', importPaths.dayReview(id))
      await at.rejectDay(heading).click()
      await reviewed
    },

    async editTime(id: string, time: string, hhmm: string) {
      await at.edit(time).click()
      await at.hour.click()
      await page.keyboard.type(hhmm)
      const reviewed = answered(page, 'PUT', importPaths.entryReview(id))
      await at.save.click()
      await reviewed
    },

    async accept(id: string) {
      await at.accept.click()
      await expect(at.acceptDialog).toBeVisible()
      const accepted = answered(page, 'POST', importPaths.accept(id))
      await at.acceptBatch.click()
      await accepted
    },
  }
}
