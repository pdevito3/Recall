import { expect, type Locator, type Page } from '@playwright/test'
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

    alwaysReplaceLayout: () =>
      at.alwaysReplace.evaluate((input) => {
        const label = input.closest('label')!
        const text = document.createRange()
        text.selectNodeContents(label.lastChild!)
        const box = text.getBoundingClientRect()
        const hint = document.getElementById(input.getAttribute('aria-describedby')!)!.getBoundingClientRect()
        const row = label.getBoundingClientRect()
        return { x: box.left + box.width / 2, y: row.top + row.height / 2, gapAboveHint: hint.top - box.bottom }
      }),

    async centerOf(element: Locator) {
      const box = (await element.boundingBox())!
      return { x: box.x + box.width / 2, y: box.y + box.height / 2 }
    },

    /** How far the 44 px tap of the × of each pill reaches into the pill after it on its row. Negative is a gap. */
    closeReachIntoNextPill: () =>
      at.knownWords.evaluateAll((pills) =>
        Math.max(
          ...pills.slice(0, -1).map((pill, index) => {
            const close = pill.querySelector('button')!.getBoundingClientRect()
            const next = pills[index + 1].getBoundingClientRect()
            const reach = close.left + close.width / 2 + 22
            return next.top < close.bottom ? reach - next.left : -Infinity
          }),
        ),
      ),

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

    async addKnownWord(term: string) {
      await at.knownWordInput.fill(term)
      const added = answered(page, 'POST', correctionsPaths.list)
      await at.knownWordInput.press('Enter')
      await added
    },

    async removeKnownWord(id: string, term: string) {
      const deleted = answered(page, 'DELETE', correctionsPaths.one(id))
      await at.removeKnownWord(term).click()
      await deleted
    },

    async remove(id: string, term: string) {
      await at.remove(term).click()
      const deleted = answered(page, 'DELETE', correctionsPaths.one(id))
      await at.confirmDelete.click()
      await deleted
    },
  }
}
