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

    async hitAreaOf(element: Locator) {
      const box = (await element.boundingBox())!
      return Math.min(box.width, box.height)
    },

    async heightOf(element: Locator) {
      return (await element.boundingBox())!.height
    },

    async widthOf(element: Locator) {
      return (await element.boundingBox())!.width
    },

    async middleOf(element: Locator) {
      const box = (await element.boundingBox())!
      return box.y + box.height / 2
    },

    async isBelow(element: Locator, above: Locator) {
      const [box, aboveBox] = [(await element.boundingBox())!, (await above.boundingBox())!]
      return box.y >= aboveBox.y + aboveBox.height
    },

    /** The count of the lines of the time after `words` in the text of `element`, up to the period that ends it. */
    linesOfTheTimeAfter: (element: Locator, words: string) =>
      element.evaluate((root, words) => {
        const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT)
        const nodes: Text[] = []
        for (let node = walker.nextNode(); node; node = walker.nextNode()) nodes.push(node as Text)
        const text = nodes.map((node) => node.data).join('')
        const from = text.indexOf(words) + words.length
        const to = text.indexOf('.', from)
        if (from < words.length || to < 0) throw new Error(`No text has ${words}`)
        const at = (offset: number): [Text, number] => {
          for (const node of nodes) {
            if (offset <= node.length) return [node, offset]
            offset -= node.length
          }
          throw new Error('The offset is after the text')
        }
        const range = document.createRange()
        range.setStart(...at(from))
        range.setEnd(...at(to))
        return new Set([...range.getClientRects()].filter((rect) => rect.width > 0).map((rect) => Math.round(rect.top))).size
      }, words),

    clipboardText: () => page.evaluate(() => navigator.clipboard.readText()),

    /** The browser refuses each copy, as a browser on plain HTTP with no copy command does. */
    refuseEachCopy: () =>
      page.addInitScript(() => {
        navigator.clipboard.writeText = () => Promise.reject(new Error('Not allowed'))
        document.execCommand = () => false
      }),

    selectedText: () => page.evaluate(() => window.getSelection()?.toString() ?? ''),

    isFocused: (element: Locator) => element.evaluate((node) => node === document.activeElement),

    async topOf(element: Locator) {
      return (await element.boundingBox())!.y
    },

    async bottomOf(element: Locator) {
      const box = (await element.boundingBox())!
      return box.y + box.height
    },

    sideScrollOf: (element: Locator) => element.evaluate((box) => box.scrollWidth - box.clientWidth),

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
