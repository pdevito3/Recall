import { expect, type Page } from '@playwright/test'
import { sessionsPaths } from '@tests/support/api/sessions-api'
import { entry, journal } from './journal.locators'

const draftPathPattern = /^\/api\/v1\/sessions\/[^/]+\/draft$/

export function journalPage(page: Page) {
  const at = journal(page)

  const aDraftSaveAnswered = () =>
    page.waitForResponse((r) => r.request().method() === 'PUT' && draftPathPattern.test(new URL(r.url()).pathname))

  return {
    at,

    async open() {
      await page.goto('/')
      await expect(at.composer).toBeVisible()
    },

    async write(words: string) {
      const saved = aDraftSaveAnswered()
      await at.composer.click()
      await page.keyboard.type(words)
      await saved
    },

    async finish() {
      await at.done.click()
    },

    async finishFromTheKeyboard() {
      await page.keyboard.press('ControlOrMeta+Enter')
    },

    async typeWithoutWaiting(words: string) {
      await at.composer.click()
      await page.keyboard.type(words)
    },

    async pickFromTheMentionList() {
      await page.keyboard.press('Enter')
    },

    async waitForAPageOfFrames() {
      await page.evaluate(async () => {
        for (let frame = 0; frame < 30; frame++) await new Promise((resolve) => requestAnimationFrame(resolve))
      })
    },

    async scrollToTheEnd() {
      await page.evaluate(() => window.scrollTo(0, document.documentElement.scrollHeight))
    },

    async reload() {
      await page.reload()
      await expect(at.composer).toBeVisible()
    },

    async pageSideScrollRange() {
      return page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)
    },
  }
}

export function entryPage(page: Page) {
  const at = entry(page)
  const putTo = (path: string) =>
    page.waitForResponse((r) => r.request().method() === 'PUT' && new URL(r.url()).pathname === path)

  return {
    at,

    async open(id: string) {
      await page.goto(`/sessions/${id}`)
      await expect(at.heading).toBeVisible()
    },

    async edit(id: string, words: string) {
      const saved = putTo(sessionsPaths.rawDraft(id))
      await at.editor.click()
      await page.keyboard.press('End')
      await page.keyboard.type(words)
      await saved
    },

    async goToAnotherEntryThroughTheJournal(preview: string) {
      await at.journalLink.click()
      await at.rowOfTheJournal(preview).click()
    },

    async goToTheJournal() {
      await at.journalLink.click()
      await expect(journal(page).composer).toBeVisible()
    },

    async goBack() {
      await page.goBack()
    },

    async goBackTwoPages() {
      await page.evaluate(() => window.history.go(-2))
    },

    async editWithoutWaiting(words: string) {
      await at.editor.click()
      await page.keyboard.press('End')
      await page.keyboard.type(words)
    },

    async startARead(id: string) {
      const read = page.waitForRequest((r) => r.method() === 'GET' && new URL(r.url()).pathname === sessionsPaths.one(id))
      await page.evaluate(() => window.dispatchEvent(new Event('visibilitychange')))
      await read
    },

    async letAnAnswerHeldForHalfASecondLand() {
      await page.evaluate(() => new Promise((resolve) => setTimeout(resolve, 700)))
    },

    async pressMood(id: string, mood: string) {
      const saved = putTo(sessionsPaths.metadata(id))
      await at.moodChip(mood).click()
      await saved
    },

    async addTopic(id: string, topic: string) {
      const saved = putTo(sessionsPaths.metadata(id))
      await at.newTopic.fill(topic)
      await at.addTopic.click()
      await saved
    },

    async confirmDelete() {
      await at.deleteButton.click()
      await at.confirmDelete.click()
      await page.waitForURL('/')
    },

    async openTheFirstVersion() {
      await at.showHistory.click()
      await at.versionView.click()
    },

    async restore(id: string) {
      const saved = putTo(sessionsPaths.rawDraft(id))
      await at.restore.click()
      await saved
    },

    async signalThatTheConnectionReturned() {
      await page.evaluate(() => window.dispatchEvent(new Event('online')))
    },
  }
}
