import { expect, type Page } from '@playwright/test'
import { sessionsPaths } from '@tests/support/api/sessions-api'
import { actorSubject } from '@tests/support/auth/actors'
import { entry, journal, unsent } from './journal.locators'

const draftPathPattern = /^\/api\/v1\/sessions\/[^/]+\/draft$/

// The first page of a run makes a cold Vite compile each module of the app, and that takes more than the 5 s of an assertion.
const firstPaintMs = 30_000

export function journalPage(page: Page) {
  const at = journal(page)

  const aDraftSaveAnswered = () =>
    page.waitForResponse((r) => r.request().method() === 'PUT' && draftPathPattern.test(new URL(r.url()).pathname))

  return {
    at,

    async open() {
      await page.goto('/')
      await expect(at.composer).toBeVisible({ timeout: firstPaintMs })
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

    cursorLineBottom: () => page.evaluate(() => window.getSelection()?.getRangeAt(0).getBoundingClientRect().bottom),

    async markTheEditor() {
      await at.composer.evaluate((editor) => {
        ;(window as unknown as { markedEditor: Element }).markedEditor = editor
      })
    },

    isTheSameEditor: () => at.composer.evaluate((editor) => (window as unknown as { markedEditor: Element }).markedEditor === editor),

    async moveTheCursorOneBack() {
      await page.keyboard.press('ArrowLeft')
    },

    async typeAfterTheCursor(words: string) {
      await page.keyboard.type(words)
    },

    settingsIsOpen: () => expect(page.getByRole('heading', { level: 1, name: 'Settings' })).toBeVisible(),

    async goToSettings() {
      await page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Settings', exact: true }).click()
      await expect(page.getByRole('heading', { level: 1, name: 'Settings' })).toBeVisible()
    },

    async goToTheJournalFromTheSidebar() {
      await page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Journal', exact: true }).click()
    },

    async reloadOnSettings() {
      await page.reload()
      await expect(page.getByRole('heading', { level: 1, name: 'Settings' })).toBeVisible()
    },

    async letAnAnswerHeldForHalfASecondLand() {
      await page.evaluate(() => new Promise((resolve) => setTimeout(resolve, 700)))
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
      await expect(at.heading).toBeVisible({ timeout: firstPaintMs })
    },

    async edit(id: string, words: string) {
      const saved = putTo(sessionsPaths.rawDraft(id))
      await at.editor.click()
      await page.keyboard.press('End')
      await page.keyboard.type(words)
      await saved
    },

    async typeWithoutWaiting(words: string) {
      await at.editor.click()
      await page.keyboard.press('End')
      await page.keyboard.type(words)
    },

    aDraftSaveAnswered: (id: string) => putTo(sessionsPaths.rawDraft(id)),

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

    aSaveAnswered(id: string) {
      return putTo(sessionsPaths.rawDraft(id))
    },

    async moveTheCursorOneBack() {
      await page.keyboard.press('ArrowLeft')
    },

    async typeAfterTheCursor(words: string) {
      await page.keyboard.type(words)
    },

    async startARead(id: string) {
      const read = page.waitForRequest((r) => r.method() === 'GET' && new URL(r.url()).pathname === sessionsPaths.one(id))
      await page.evaluate(() => window.dispatchEvent(new Event('visibilitychange')))
      await read
    },

    async reloadOnSettings() {
      await page.reload()
      await expect(page.getByRole('heading', { level: 1, name: 'Settings' })).toBeVisible()
    },

    async letAnAnswerHeldForHalfASecondLand() {
      await page.evaluate(() => new Promise((resolve) => setTimeout(resolve, 700)))
    },

    async pressMood(id: string, mood: string) {
      const saved = putTo(sessionsPaths.metadata(id))
      await at.moodChip(mood).click()
      await saved
    },

    sideScrollRange: () => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth),

    async leaveForTheJournal() {
      await at.goToJournal.click()
      await page.waitForURL('/')
    },

    async removeTopic(id: string, topic: string) {
      const saved = putTo(sessionsPaths.metadata(id))
      await at.removeTopic(topic).click()
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

interface Unsent {
  words: string
  sessionId: string | null
  clientSessionId?: string
  refusal?: string
  owner?: string
}

export function unsentWords(page: Page) {
  const at = unsent(page)

  const leftInStorage = (key: string, stored: string) =>
    page.addInitScript(([k, v]) => {
      const flag = `seeded.${k}`
      if (sessionStorage.getItem(flag)) return
      sessionStorage.setItem(flag, '1')
      localStorage.setItem(k, v)
    }, [key, stored])

  return {
    at,
    leftInStorage,

    /** A change that an earlier load of the page left in the outbox. It goes in before the app starts, and only once. */
    async leftInTheOutbox({ words, sessionId, clientSessionId = crypto.randomUUID(), refusal, owner = actorSubject }: Unsent) {
      const key = `recall.unsent.${owner}.${clientSessionId}.raw`
      await leftInStorage(key, JSON.stringify({
        key: `${clientSessionId}.raw`,
        channel: 'raw',
        clientSessionId,
        startedAt: '2026-11-06T15:30:00.000Z',
        sessionId,
        baseRevision: 1,
        latestRevision: 1,
        change: { value: words, savedAt: '2026-11-06T15:31:00.000Z' },
        refusal,
      }))
      return key
    },

    stored: (key: string) => page.evaluate((k) => localStorage.getItem(k), key),
    keys: () => page.evaluate(() => Object.keys(localStorage).filter((key) => key.startsWith('recall.unsent.'))),
    clientSessionIdOfTheHold: () =>
      page.evaluate(() => {
        const entries = Object.keys(localStorage).filter((key) => key.startsWith('recall.unsent.')).map((key) => JSON.parse(localStorage.getItem(key)!) as { clientSessionId: string; held?: string | null })
        return entries.find(({ held }) => held != null)!.clientSessionId
      }),
    settingKeys: () => page.evaluate(() => Object.keys(localStorage).filter((key) => key.startsWith('recall.unsent-setting.'))),
    composerPointer: () => page.evaluate(() => sessionStorage.getItem('recall.composer')),
    clipboardText: () => page.evaluate(() => navigator.clipboard.readText()),

    /** As much as the storage of this origin takes, so that the next write of the words fails. */
    async fillTheStorage() {
      await page.evaluate(() => {
        let size = 1 << 20
        let index = 0
        while (size >= 1) {
          try {
            localStorage.setItem(`fill.${index++}`, 'x'.repeat(size))
          } catch {
            size >>= 1
          }
        }
      })
    },

    warnsAtAClose: () =>
      page.evaluate(() => {
        const close = new Event('beforeunload', { cancelable: true })
        window.dispatchEvent(close)
        return close.defaultPrevented
      }),

    countTheDialogs() {
      let dialogs = 0
      page.on('dialog', (dialog) => {
        dialogs += 1
        void dialog.accept()
      })
      return () => dialogs
    },

    signOutInAnotherTab: () => page.evaluate(() => new BroadcastChannel('recall.session').postMessage('signed-out')),

    async openSettings() {
      await page.goto('/settings')
      await expect(at.settingsHeading).toBeVisible({ timeout: firstPaintMs })
    },

    async logOut() {
      await at.userMenu.click()
      await at.logout.click()
    },

    async comeBackToTheTab() {
      await page.evaluate(() => window.dispatchEvent(new Event('visibilitychange')))
    },

    async signalThatTheConnectionReturned() {
      await page.evaluate(() => window.dispatchEvent(new Event('online')))
    },

    async letAnAnswerLand() {
      await page.evaluate(() => new Promise((resolve) => setTimeout(resolve, 700)))
    },

    openTheNewEntry: () => at.openNewEntry.click(),
    dismissTheMovedNotice: () => at.dismissMoved.click(),
    copyTheRefusedWords: () => at.copyRefused.click(),
    discardTheRefusedWords: () => at.discardRefused.click(),
    answerTheLogoutDialog: (choice: string) => at.logoutChoice(choice).click(),
    reachTheSignInPageForAnotherPerson: () => page.waitForURL(/\/auth\/login\?anotherPerson=1/),
    reachTheSignedOutPage: () => page.waitForURL(/signedOut=1/),
  }
}
