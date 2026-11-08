import type { Page } from '@playwright/test'
import { sessionsApi, sessionsPaths } from '@tests/support/api/sessions-api'
import { aSession } from '@tests/support/builders/session-builder'
import { expect, test, type Api } from '@tests/support/fixtures'
import type { SessionDto } from '@/api/generated/models/types/session-dto'
import { entryPage } from './journal.tasks'

/** One tab of a browser edits an entry at a time (ADR 0022). Each test runs Web Locks, because localhost is a secure context. */

const lastWords = (api: Api, id: string) => JSON.stringify(api.last('put', sessionsPaths.rawDraft(id))?.body)
const hasUnsentKey = (page: Page) => page.evaluate(() => Object.keys(localStorage).some((key) => key.startsWith('recall.unsent.')))
/** The entry as the server holds it after a save of these words. */
const savedAs = (session: SessionDto, words: string, revision: number) => ({ ...session, rawText: aSession().saying(words).build().rawText, rawDraftRevision: revision, rawLatestRevision: revision })

async function typeIn(page: Page, words: string) {
  const tab = entryPage(page)
  await tab.at.editor.click()
  await page.keyboard.press('End')
  await page.keyboard.type(words)
  return tab
}

test.describe('an entry open in two tabs', () => {
  test('the second tab shows the words of the editor with no edit, and Edit here by keyboard moves the editor with each word', async ({ page, context, api }) => {
    const session = aSession().atRevision(4).saying('Mine').build()
    // The save of the first tab is still running when the person moves.
    api.mock(sessionsApi.oneIs(session), { ...sessionsApi.draftSaveWins(session, 5), neverAnswers: true })
    await entryPage(page).open(session.id)
    const first = await typeIn(page, ' typed in the first tab')
    await expect.poll(() => lastWords(api, session.id)).toContain('Mine typed in the first tab')

    const secondPage = await context.newPage()
    const second = entryPage(secondPage)
    await second.open(session.id)

    await expect(second.at.openInAnotherTab).toBeVisible()
    await expect(second.at.editor).toHaveCount(0)
    await expect(second.at.wordsOfAnotherTab).toContainText('Mine typed in the first tab')
    await expect(second.at.wordsOfAnotherTab).toHaveAttribute('aria-readonly', 'true')
    await expect(second.at.editHere).toHaveAccessibleDescription(/This entry is open in another tab/)

    api.mock(sessionsApi.oneIs(savedAs(session, 'Mine typed in the first tab', 5)), sessionsApi.draftSaveWins(session, 5))
    await second.at.editHere.focus()
    await secondPage.keyboard.press('Enter')

    await expect(second.at.editor).toBeFocused()
    await expect(second.at.editor).toContainText('Mine typed in the first tab')
    await expect(first.at.nowEditedInAnotherTab).toBeVisible()
    await expect(first.at.editor).toHaveCount(0)
    // The editor had the focus, so the focus goes to Edit here, which names why.
    await expect(first.at.editHere).toBeFocused()
    await expect(first.at.editHere).toHaveAccessibleDescription(/You now edit this entry in another tab/)
    await expect(first.at.wordsOfAnotherTab).toContainText('Mine typed in the first tab')

    api.mock(sessionsApi.oneIs(savedAs(session, 'Mine typed in the first tab and in the second', 6)), sessionsApi.draftSaveWins(session, 6))
    await secondPage.keyboard.press('End')
    await secondPage.keyboard.type(' and in the second')
    await expect.poll(() => lastWords(api, session.id), { timeout: 10_000 }).toContain('Mine typed in the first tab and in the second')
    await expect(first.at.wordsOfAnotherTab).toContainText('Mine typed in the first tab and in the second')
  })

  test('after a save, the second tab shows the saved words and not the older words', async ({ page, context, api }) => {
    const session = aSession().atRevision(4).saying('Mine').build()
    api.mock(sessionsApi.oneIs(session), { ...sessionsApi.draftSaveWins(session, 5), delayMs: 3_000 })
    await entryPage(page).open(session.id)
    await typeIn(page, ' and saved')
    await expect.poll(() => lastWords(api, session.id)).toContain('Mine and saved')
    const secondPage = await context.newPage()
    const second = entryPage(secondPage)
    await second.open(session.id)
    await expect(second.at.wordsOfAnotherTab).toContainText('Mine and saved')
    // The server holds the words before its answer comes back.
    api.mock(sessionsApi.oneIs(savedAs(session, 'Mine and saved', 5)))
    // Each change of the page, so that a moment with the older words shows too.
    await secondPage.evaluate(() => {
      const shown: string[] = []
      Object.assign(window, { shownWords: shown })
      new MutationObserver(() => {
        const words = document.querySelector('[aria-label="Your words, open in another tab"]')?.textContent
        if (words !== undefined && words !== null) shown.push(words)
      }).observe(document.body, { childList: true, subtree: true, characterData: true })
    })

    const reads = api.all('get', sessionsPaths.one(session.id)).length
    // The answer of the save removes the key of the first tab, and the page reads the Draft.
    await expect.poll(() => hasUnsentKey(secondPage), { timeout: 10_000 }).toBe(false)
    await expect.poll(() => api.all('get', sessionsPaths.one(session.id)).length).toBeGreaterThan(reads)

    await expect(second.at.wordsOfAnotherTab).toContainText('Mine and saved')
    expect(await secondPage.evaluate(() => (window as unknown as { shownWords: string[] }).shownWords)).not.toContain('Mine')
    await second.at.editHere.click()
    await expect(second.at.editor).toContainText('Mine and saved')
  })

  test('a close of the editor gives the entry to the tab that shows it, with the words of the closed tab', async ({ context, api }) => {
    const session = aSession().atRevision(4).saying('Mine').build()
    // No save answers, so the words stay only in the key of the first tab.
    api.mock(sessionsApi.oneIs(session), { ...sessionsApi.draftSaveWins(session, 5), neverAnswers: true })
    const firstPage = await context.newPage()
    await entryPage(firstPage).open(session.id)
    await typeIn(firstPage, ' before the close')
    await expect.poll(() => lastWords(api, session.id)).toContain('Mine before the close')
    const secondPage = await context.newPage()
    const second = entryPage(secondPage)
    await second.open(session.id)
    await expect(second.at.openInAnotherTab).toBeVisible()

    api.mock(sessionsApi.draftSaveWins(session, 5))
    await firstPage.close()

    await expect(second.at.editor).toContainText('Mine before the close')
    await expect(second.at.openInAnotherTab).toHaveCount(0)
    await expect.poll(() => hasUnsentKey(secondPage), { timeout: 10_000 }).toBe(false)
  })
})
