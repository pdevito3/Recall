import { expect, type Page } from '@playwright/test'
import { correctionsPaths } from '@tests/support/api/corrections-api'
import { corrections } from './corrections.locators'

const answered = (page: Page, method: string, path: string) =>
  page.waitForResponse((r) => r.request().method() === method && new URL(r.url()).pathname === path)

export function correctionsPage(page: Page) {
  const at = corrections(page)

  return {
    at,

    async open(search?: string) {
      await page.goto(search ? `/corrections?search=${encodeURIComponent(search)}` : '/corrections')
      await expect(at.heading).toBeVisible()
    },

    sideScrollRange: () => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth),

    async add(term: string, mishearings: string[], alwaysReplace = false) {
      await at.add.click()
      await at.termField.fill(term)
      await at.mishearingsField.fill(mishearings.join('\n'))
      // React Aria hides the input under its box, as the checkboxes of the settings page do.
      if (alwaysReplace) await at.alwaysReplace.click({ force: true })
      const added = answered(page, 'POST', correctionsPaths.list)
      await at.save.click()
      await added
    },

    async edit(id: string, term: string, change: { mishearings?: string[]; alwaysReplace?: boolean }) {
      await at.edit(term).click()
      if (change.mishearings) await at.mishearingsField.fill(change.mishearings.join('\n'))
      if (change.alwaysReplace !== undefined && (await at.alwaysReplace.isChecked()) !== change.alwaysReplace) {
        await at.alwaysReplace.click({ force: true })
      }
      const saved = answered(page, 'PUT', correctionsPaths.one(id))
      await at.save.click()
      await saved
    },

    async remove(id: string, term: string) {
      await at.remove(term).click()
      const deleted = answered(page, 'DELETE', correctionsPaths.one(id))
      await at.confirmDelete.click()
      await deleted
    },
  }
}
