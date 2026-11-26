import { readFileSync } from 'node:fs'

import { anImportBatch, anImportDay, anImportEntry, documentOf, importPaths, importsApi } from '@tests/support/api/imports-api'
import { aSetting, settingsApi } from '@tests/support/api/settings-api'
import { aProblem } from '@tests/support/builders/problem-details-builder'
import { expect, test, type Api } from '@tests/support/fixtures'
import { importsPage } from './imports.tasks'

const skillFile = readFileSync(new URL('../../../src/domain/imports/recall-import.md', import.meta.url), 'utf8')

const diary = anImportBatch('imp_1', 'Diary 2024', { entryCount: 3, version: 4 })
const friday = 'Friday, March 1, 2024'
const saturday = 'Saturday, March 2, 2024'

const coffee = anImportEntry('c1', '2024-03-01', 'Coffee on the porch.', { moods: ['Calm'], topics: ['Morning'] })
const walk = anImportEntry('c2', '2024-03-01', 'Long walk.', { position: 1, time: null, startsAt: '2024-03-01T17:01:00Z' })
const london = anImportEntry('c3', '2024-03-02', 'Flew to London.', { time: '21:00', timeZone: 'Europe/London', startsAt: '2024-03-02T21:00:00Z' })
const twoDays = [anImportDay('2024-03-01', 2), anImportDay('2024-03-02', 1)]

const reviewOf = (api: Api) => {
  api.mock(importsApi.listIs([diary]), importsApi.daysAre(diary.id, twoDays, 4), importsApi.entriesOfAnyRangeAre(diary.id, [coffee, walk, london]))
}

test.describe('the list of import batches', () => {
  test('the sidebar opens the list, and each batch shows its name, count, zone, and state', { tag: '@IMP-33' }, async ({ page, api }) => {
    const accepted = anImportBatch('imp_2', 'Diary 2023', {
      state: 'Accepted',
      acceptedAt: '2026-11-21T10:00:00Z',
      entryCount: 0,
      result: { sessionsMade: 5, entriesRejected: 1, daysRejected: 0, skippedInJournal: 0, skippedDeleted: 0 },
    })
    api.mock(importsApi.listIs([diary, accepted]))
    const imports = importsPage(page)
    await page.goto('/')

    await imports.at.nav.click()

    await expect(imports.at.heading).toBeVisible()
    await expect(imports.at.batch('Diary 2024')).toContainText('Open3 entries. Times in Eastern Time.')
    await expect(imports.at.batch('Diary 2023')).toContainText('In your journal5 entries made in your journal. Times in Eastern Time.')
    await expect(imports.at.batchLink('Diary 2024')).toHaveAttribute('href', '/imports/imp_1')
  })

  test('with no batch, the list says that the batches of an agent show here', { tag: '@IMP-33' }, async ({ page, api }) => {
    api.mock(importsApi.listIs([]))
    const imports = importsPage(page)

    await imports.openList()

    await expect(imports.at.empty).toHaveText('You have no import batches. When an agent opens a batch, you review it here.')
  })

  test('discard asks first with the name and the count, sends the delete, and the batch leaves the list', { tag: '@IMP-34' }, async ({ page, api }) => {
    api.mock(importsApi.listIs([diary]), importsApi.discards(diary.id))
    const imports = importsPage(page)
    await imports.openList()
    await imports.at.discardOf('Diary 2024').click()
    await expect(imports.at.discardDialog('Diary 2024')).toContainText('Discard deletes this batch and its 3 entries. None of them go into your journal. You cannot undo a discard.')
    api.mock(importsApi.listIs([]))

    await imports.at.discardBatch.click()

    await expect(imports.at.text('Batch discarded')).toBeVisible()
    await expect(imports.at.batch('Diary 2024')).toHaveCount(0)
    expect(api.all('delete', importPaths.one(diary.id))).toHaveLength(1)
    await expect(imports.at.discardOf('Diary 2024')).toHaveCount(0)
  })

  test('a long name with no spaces stays in the page on a phone', { tag: '@IMP-33' }, async ({ page, api }) => {
    await page.setViewportSize({ width: 390, height: 844 })
    const name = 'D'.repeat(200)
    api.mock(importsApi.listIs([anImportBatch('imp_3', name, { entryCount: 1 })]))
    const imports = importsPage(page)

    await imports.openList()

    await expect(imports.at.discardOf(name)).toBeInViewport({ ratio: 1 })
    expect(await imports.sideScrollRange()).toBe(0)
  })

  test('on a phone, the date and the time when a batch opened and was accepted stay on one line', { tag: '@IMP-33' }, async ({ page, api }) => {
    await page.setViewportSize({ width: 390, height: 844 })
    const accepted = anImportBatch('imp_2', 'Diary 2023', {
      state: 'Accepted',
      openedAt: '2026-11-20T22:14:00Z',
      acceptedAt: '2026-11-26T22:14:00Z',
      result: { sessionsMade: 5, entriesRejected: 1, daysRejected: 0, skippedInJournal: 0, skippedDeleted: 0 },
    })
    api.mock(importsApi.listIs([accepted]))
    const imports = importsPage(page)

    await imports.openList()

    for (const words of ['Opened on ', 'Accepted on ']) {
      expect(await imports.linesOfTheTimeAfter(imports.at.batch('Diary 2023'), words)).toBe(1)
    }
    await imports.at.batchLink('Diary 2023').click()
    await expect(imports.at.result).toBeVisible()
    expect(await imports.linesOfTheTimeAfter(imports.at.resultNote, 'Accepted on ')).toBe(1)
  })

  test('on a phone, the name of a batch in the list has a hit area of at least 44 px high', { tag: '@IMP-33' }, async ({ page, api }) => {
    await page.setViewportSize({ width: 390, height: 844 })
    api.mock(importsApi.listIs([diary]))
    const imports = importsPage(page)

    await imports.openList()

    expect(await imports.heightOf(imports.at.batchLink('Diary 2024'))).toBeGreaterThanOrEqual(44)
  })
})

test.describe('the recall-import skill', () => {
  test('the panel comes after the list, and Copy skill puts the whole file on the clipboard with the skill closed', { tag: '@IMP-46' }, async ({ page, context, api }) => {
    await context.grantPermissions(['clipboard-read', 'clipboard-write'])
    api.mock(importsApi.listIs([diary]))
    const imports = importsPage(page)
    await imports.openList()

    await imports.at.copySkill.click()

    await expect(imports.at.text('Skill copied')).toBeVisible()
    await expect.poll(() => imports.clipboardText()).toBe(skillFile)
    expect(await imports.isFocused(imports.at.copySkill)).toBe(true)
    await expect(imports.at.skillToggle).toHaveAttribute('aria-expanded', 'false')
    await expect(imports.at.skillText).toBeHidden()
    expect(await imports.at.skillText.textContent()).toBe(skillFile)
    expect(await imports.topOf(imports.at.skillPanel)).toBeGreaterThan(await imports.bottomOf(imports.at.batches))
  })

  test('the panel shows the MCP URL of this Recall, and Copy MCP URL puts it on the clipboard', { tag: '@IMP-46' }, async ({ page, context, api, baseURL }) => {
    await context.grantPermissions(['clipboard-read', 'clipboard-write'])
    api.mock(importsApi.listIs([]))
    const imports = importsPage(page)
    await imports.openList()

    await imports.at.copyMcpUrl.click()

    await expect(imports.at.text('MCP URL copied')).toBeVisible()
    await expect.poll(() => imports.clipboardText()).toBe(`${new URL(baseURL!).origin}/mcp`)
    await expect(imports.at.mcpUrl).toContainText(`${new URL(baseURL!).origin}/mcp`)
  })

  test('Copy command puts the Claude Code command with the MCP URL of this Recall on the clipboard', { tag: '@IMP-46' }, async ({ page, context, api, baseURL }) => {
    await context.grantPermissions(['clipboard-read', 'clipboard-write'])
    api.mock(importsApi.listIs([]))
    const imports = importsPage(page)
    const command = `claude mcp add --transport http recall ${new URL(baseURL!).origin}/mcp --header "Authorization: Bearer <your token>"`
    await imports.openList()

    await imports.at.copyCommand.click()

    await expect(imports.at.text('Command copied')).toBeVisible()
    await expect.poll(() => imports.clipboardText()).toBe(command)
    await expect(imports.at.commandText(command)).toBeVisible()
    expect(await imports.isFocused(imports.at.copyCommand)).toBe(true)
  })

  test('when the browser refuses the copy of the MCP URL, Recall selects the whole URL', { tag: '@IMP-46' }, async ({ page, api, baseURL }) => {
    api.mock(importsApi.listIs([]))
    const imports = importsPage(page)
    await imports.refuseEachCopy()
    await imports.openList()

    await imports.at.copyMcpUrl.click()

    await expect(imports.at.text('The browser did not copy the MCP URL. Recall selected it, so copy it by hand.')).toBeVisible()
    expect(await imports.selectedText()).toBe(`${new URL(baseURL!).origin}/mcp`)
  })

  for (const size of [{ width: 1280, height: 800 }, { width: 390, height: 844 }]) {
    test(`at ${size.width} px, the text of each box of the panel lines up with the text of a batch card, with the same inner padding`, { tag: '@IMP-46' }, async ({ page, api }) => {
      await page.setViewportSize(size)
      api.mock(importsApi.listIs([diary]))
      const imports = importsPage(page)
      await imports.openList()

      const cardText = await imports.textLeftOf(imports.at.batchLink('Diary 2024'))
      expect(await imports.textLeftOf(imports.at.mcpUrlValue)).toBe(cardText)
      expect(await imports.textLeftOf(imports.at.commandValue)).toBe(cardText)
      expect(await imports.textLeftOf(imports.at.skillToggle)).toBe(cardText)
      // The card is inside the border of the list, and each box of the panel has its own border of 1 px.
      const { right: padding } = await imports.insetOf(imports.at.discardOf('Diary 2024'), imports.at.batch('Diary 2024'))
      // On a phone, a copy button wraps below its value. Else it is at the right, in the middle of the box.
      const sideOfButton = size.width < 640 ? 'bottom' : 'right'
      expect((await imports.insetOf(imports.at.copyMcpUrl, imports.at.mcpUrl))[sideOfButton]).toBe(padding + 1)
      expect((await imports.insetOf(imports.at.copyCommand, imports.at.claudeCodeCommand))[sideOfButton]).toBe(padding + 1)
      expect(await imports.insetOf(imports.at.copySkill, imports.at.skillRow)).toMatchObject({ [sideOfButton]: padding, bottom: padding })

      await imports.at.skillToggle.click()
      await expect(imports.at.skillToggle).toHaveAttribute('aria-expanded', 'true')
      expect(await imports.textLeftOf(imports.at.skillToggle)).toBe(cardText)
    })
  }

  test('the skill opens, and its box takes the keyboard focus after Copy skill', { tag: '@IMP-46' }, async ({ page, api }) => {
    api.mock(importsApi.listIs([diary]))
    const imports = importsPage(page)
    await imports.openList()

    await imports.at.skillToggle.click()
    await imports.at.copySkill.focus()
    await page.keyboard.press('Tab')

    await expect(imports.at.skillToggle).toHaveAttribute('aria-expanded', 'true')
    await expect(imports.at.skillText).toBeVisible()
    await expect(imports.at.skillText).toBeFocused()
  })

  test('when the browser refuses the copy, the skill opens with its whole text selected', { tag: '@IMP-46' }, async ({ page, api }) => {
    api.mock(importsApi.listIs([diary]))
    const imports = importsPage(page)
    await imports.refuseEachCopy()
    await imports.openList()

    await imports.at.copySkill.click()

    await expect(imports.at.text('The browser did not copy the skill. Recall selected the whole text of the skill, so copy it by hand.')).toBeVisible()
    await expect(imports.at.skillText).toBeVisible()
    // A copy of a selection drops the line break at the end of the file, and keeps each other line.
    expect(await imports.selectedText()).toBe(skillFile.trimEnd())
  })

  test('on a phone, the panel wraps in the page, and its buttons have a hit area of at least 44 px', { tag: '@IMP-46' }, async ({ page, api }) => {
    await page.setViewportSize({ width: 390, height: 844 })
    api.mock(importsApi.listIs([diary]))
    const imports = importsPage(page)
    await imports.openList()

    await imports.at.skillToggle.click()

    await expect(imports.at.skillText).toBeVisible()
    expect(await imports.isInsideTheWindowWidth(imports.at.skillPanel)).toBe(true)
    expect(await imports.sideScrollRange()).toBe(0)
    expect(await imports.sideScrollOf(imports.at.skillText)).toBe(0)
    expect(await imports.hitAreaOf(imports.at.copySkill)).toBeGreaterThanOrEqual(44)
    expect(await imports.hitAreaOf(imports.at.copyMcpUrl)).toBeGreaterThanOrEqual(44)
    expect(await imports.hitAreaOf(imports.at.copyCommand)).toBeGreaterThanOrEqual(44)
    expect(await imports.hitAreaOf(imports.at.skillToggle)).toBeGreaterThanOrEqual(44)
  })
})

test.describe('the review of a batch', () => {
  test('a batch of more than one page reads whole days with from and to, and the page is in the URL', { tag: '@IMP-35' }, async ({ page, api }) => {
    api.mock(
      importsApi.listIs([diary]),
      importsApi.daysAre(diary.id, [anImportDay('2024-03-01', 150), anImportDay('2024-03-02', 100)]),
      importsApi.entriesOfAnyRangeAre(diary.id, [coffee]),
    )
    const imports = importsPage(page)
    await imports.openReviewWithEntries(diary.id, 'Diary 2024')
    await expect(imports.at.entry('8:30 AM')).toBeVisible()
    await expect(imports.at.pager).toContainText('Page 1 of 2: Mar 1, 2024')
    expect(api.last('get', importPaths.entries(diary.id))?.query.toString()).toBe('from=2024-03-01&to=2024-03-01')
    api.mock(importsApi.entriesOfAnyRangeAre(diary.id, [london]))

    await imports.at.nextPage.click()

    await expect(imports.at.entry('9:00 PM')).toBeVisible()
    await expect(page).toHaveURL(/[?&]page=2/)
    expect(api.last('get', importPaths.entries(diary.id))?.query.toString()).toBe('from=2024-03-02&to=2024-03-02')
    await expect(imports.at.day(friday)).toHaveCount(0)
  })

  test('the entries show by day, each with its time in its zone, its words, Moods, and Topics', { tag: ['@IMP-35', '@IMP-36'] }, async ({ page, api }) => {
    reviewOf(api)
    const imports = importsPage(page)

    await imports.openReviewWithEntries(diary.id, 'Diary 2024')

    await expect(imports.at.entriesOf(friday)).toHaveCount(2)
    await expect(imports.at.entry('8:30 AM')).toContainText('8:30 AMCoffee on the porch.CalmMorning')
    await expect(imports.at.entry('12:01 PM')).toContainText('12:01 PMNo time given. Set from its place in the day.Long walk.')
    await expect(imports.at.entriesOf(saturday)).toHaveCount(1)
    await expect(imports.at.entry('9:00 PM')).toContainText('9:00 PMUnited Kingdom TimeFlew to London.')
  })

  test('the words of a long entry with no spaces stay in the page on a phone', { tag: '@IMP-36' }, async ({ page, api }) => {
    await page.setViewportSize({ width: 390, height: 844 })
    const long = anImportEntry('c9', '2024-03-01', 'w'.repeat(2000), { moods: ['m'.repeat(200)] })
    api.mock(importsApi.listIs([diary]), importsApi.daysAre(diary.id, [anImportDay('2024-03-01', 1)]), importsApi.entriesOfAnyRangeAre(diary.id, [long]))
    const imports = importsPage(page)

    await imports.openReviewWithEntries(diary.id, 'Diary 2024')

    expect(await imports.isInsideTheWindowWidth(imports.at.reject('8:30 AM'))).toBe(true)
    expect(await imports.isInsideTheWindowWidth(imports.at.rejectDay(friday))).toBe(true)
    await expect(imports.at.entry('8:30 AM')).toContainText('m'.repeat(200))
    expect(await imports.sideScrollRange()).toBe(0)
  })

  test('an entry that the import changed after the edit says that the accept keeps the edit', { tag: '@IMP-37' }, async ({ page, api }) => {
    const flagged = { ...coffee, review: { rejected: false, words: documentOf('Mine.'), time: '09:00' }, wordsChangedAfterEdit: true, timeChangedAfterEdit: true }
    api.mock(importsApi.listIs([diary]), importsApi.daysAre(diary.id, twoDays), importsApi.entriesOfAnyRangeAre(diary.id, [flagged, walk, london]))
    const imports = importsPage(page)

    await imports.openReviewWithEntries(diary.id, 'Diary 2024')

    await expect(imports.at.entry('8:30 AM')).toContainText('The import changed the words after your edit. The accept keeps your edit. To use the new words, choose “Edit”, then “Use the import words”.')
    await expect(imports.at.entry('8:30 AM')).toContainText('The import changed the time after your edit. The accept keeps your time. To use the new time, choose “Edit”, then “Use the import time”.')
    await expect(imports.at.entry('12:01 PM')).not.toContainText('The import changed')
  })

  test('an edit of the time sends the whole review, and the entry shows the time of the reply', { tag: '@IMP-38' }, async ({ page, api }) => {
    reviewOf(api)
    const reply = { ...coffee, review: { rejected: false, words: null, time: '09:15' }, startsAt: '2024-03-01T14:15:00Z' }
    api.mock(importsApi.reviewsEntry(diary.id, reply))
    const imports = importsPage(page)
    await imports.openReviewWithEntries(diary.id, 'Diary 2024')
    api.mock(importsApi.entriesOfAnyRangeAre(diary.id, [reply, walk, london]))

    await imports.editTime(diary.id, '8:30 AM', '0915')

    expect(api.last('put', importPaths.entryReview(diary.id))?.body).toEqual({ clientSessionId: 'c1', rejected: false, words: null, time: '09:15' })
    await expect(imports.at.entry('9:15 AM')).toContainText('9:15 AMEdited')
    await expect(imports.at.edit('9:15 AM')).toBeFocused()
  })

  test('the hint of the time has the width of the card, not of the time field', { tag: '@IMP-38' }, async ({ page, api }) => {
    reviewOf(api)
    const imports = importsPage(page)
    await imports.openReviewWithEntries(diary.id, 'Diary 2024')

    await imports.at.edit('12:01 PM').click()

    const hint = imports.at.timeHint
    await expect(hint).toHaveText('In Eastern Time. Leave it empty to keep the place of the entry in its day.')
    expect(await imports.heightOf(hint)).toBeLessThan(30)
    expect(await imports.widthOf(imports.at.timeInput)).toBeLessThanOrEqual(240)
  })

  test('Use the import time sends null for the time and keeps the words of the last edit', { tag: '@IMP-38' }, async ({ page, api }) => {
    const mine = documentOf('Mine.')
    const edited = { ...coffee, document: mine, review: { rejected: false, words: mine, time: '09:00' }, startsAt: '2024-03-01T14:00:00Z' }
    api.mock(importsApi.listIs([diary]), importsApi.daysAre(diary.id, twoDays), importsApi.entriesOfAnyRangeAre(diary.id, [edited, walk, london]))
    const reply = { ...edited, review: { rejected: false, words: mine, time: null }, startsAt: coffee.startsAt }
    api.mock(importsApi.reviewsEntry(diary.id, reply))
    const imports = importsPage(page)
    await imports.openReviewWithEntries(diary.id, 'Diary 2024')
    api.mock(importsApi.entriesOfAnyRangeAre(diary.id, [reply, walk, london]))
    await imports.at.edit('9:00 AM').click()

    await imports.at.useImportTime.click()

    await expect(imports.at.editForm).toHaveCount(0)
    expect(api.last('put', importPaths.entryReview(diary.id))?.body).toEqual({ clientSessionId: 'c1', rejected: false, words: mine, time: null })
    await expect(imports.at.entry('8:30 AM')).toContainText('Mine.')
  })

  test('a refused edit shows the message of the Server in the form, and the form stays', { tag: ['@IMP-38', '@IMP-44'] }, async ({ page, api }) => {
    reviewOf(api)
    const refused = 'Give the entry some words, or reject it to leave it out.'
    api.mock(importsApi.refusesEntryReview(diary.id, aProblem().invalid({ words: [refused] }).build()))
    const imports = importsPage(page)
    await imports.openReviewWithEntries(diary.id, 'Diary 2024')
    await imports.at.edit('8:30 AM').click()
    await imports.at.words.click()
    await page.keyboard.press('ControlOrMeta+a')
    await page.keyboard.press('Delete')

    await imports.at.save.click()

    await expect(imports.at.formAlert).toHaveText(refused)
    await expect(imports.at.editForm).toBeVisible()
  })

  test('Reject sends the whole review, and the entry shows Rejected with Keep', { tag: '@IMP-39' }, async ({ page, api }) => {
    reviewOf(api)
    const rejected = { ...walk, review: { rejected: true, words: null, time: null }, isRejected: true }
    api.mock(importsApi.reviewsEntry(diary.id, rejected))
    const imports = importsPage(page)
    await imports.openReviewWithEntries(diary.id, 'Diary 2024')
    api.mock(
      importsApi.daysAre(diary.id, [anImportDay('2024-03-01', 2, { rejectedCount: 1 }), anImportDay('2024-03-02', 1)], 5),
      importsApi.entriesOfAnyRangeAre(diary.id, [coffee, rejected, london]),
    )

    await imports.reject(diary.id, '12:01 PM')

    expect(api.last('put', importPaths.entryReview(diary.id))?.body).toEqual({ clientSessionId: 'c2', rejected: true, words: null, time: null })
    await expect(imports.at.entry('12:01 PM')).toContainText('Rejected')
    await expect(imports.at.keep('12:01 PM')).toBeVisible()
    await expect(imports.at.counts).toHaveText('3 entries: 2 kept, 1 rejected')
  })

  test('Reject day sends the review of the day, the heading says the day is rejected, and each entry says rejected with the day', { tag: ['@IMP-40', '@IMP-41'] }, async ({ page, api }) => {
    reviewOf(api)
    api.mock(importsApi.reviewsDay(diary.id, '2024-03-01', true))
    const imports = importsPage(page)
    await imports.openReviewWithEntries(diary.id, 'Diary 2024')
    const held = { isRejected: true }
    api.mock(
      importsApi.daysAre(diary.id, [anImportDay('2024-03-01', 2, { rejected: true }), anImportDay('2024-03-02', 1)], 5),
      importsApi.entriesOfAnyRangeAre(diary.id, [{ ...coffee, ...held }, { ...walk, ...held }, london]),
    )

    await imports.rejectDay(diary.id, friday)

    expect(api.last('put', importPaths.dayReview(diary.id))?.body).toEqual({ date: '2024-03-01', rejected: true })
    await expect(imports.at.entry('8:30 AM')).toContainText('Rejected with the day')
    await expect(imports.at.entry('12:01 PM')).toContainText('Rejected with the day')
    await expect(imports.at.dayRegion(friday)).toContainText('2 entries, day rejected')
    await expect(imports.at.reject('8:30 AM')).toHaveCount(0)
    await expect(imports.at.keepDay(friday)).toBeVisible()
    await expect(imports.at.counts).toHaveText('3 entries: 1 kept, 2 rejected')
  })

  test('on a phone, Reject day and Keep day have a hit area of at least 44 by 44 px', { tag: '@IMP-40' }, async ({ page, api }) => {
    await page.setViewportSize({ width: 390, height: 844 })
    api.mock(importsApi.listIs([diary]), importsApi.daysAre(diary.id, [anImportDay('2024-03-01', 2), anImportDay('2024-03-02', 1, { rejected: true })], 4), importsApi.entriesOfAnyRangeAre(diary.id, [coffee, walk, london]))
    const imports = importsPage(page)

    await imports.openReviewWithEntries(diary.id, 'Diary 2024')

    for (const button of [imports.at.rejectDay(friday), imports.at.keepDay(saturday)]) {
      expect(await imports.hitAreaOf(button)).toBeGreaterThanOrEqual(44)
    }
  })

  test('on a phone, a day heading on two lines has Keep day on its first line', { tag: '@IMP-40' }, async ({ page, api }) => {
    await page.setViewportSize({ width: 390, height: 844 })
    const wednesday = 'Wednesday, September 30, 2026'
    api.mock(
      importsApi.listIs([diary]),
      importsApi.daysAre(diary.id, [anImportDay('2026-09-30', 12, { rejected: true })], 4),
      importsApi.entriesOfAnyRangeAre(diary.id, [{ ...coffee, date: '2026-09-30', isRejected: true }]),
    )
    const imports = importsPage(page)

    await imports.openReviewWithEntries(diary.id, 'Diary 2024')

    await expect(imports.at.dayRegion(wednesday)).toContainText('12 entries, day rejected')
    expect(await imports.heightOf(imports.at.day(wednesday))).toBeLessThan(30)
    expect(await imports.isBelow(imports.at.dayCounts(wednesday, '12 entries, day rejected'), imports.at.day(wednesday))).toBe(true)
    expect(Math.abs((await imports.middleOf(imports.at.keepDay(wednesday))) - (await imports.middleOf(imports.at.day(wednesday))))).toBeLessThanOrEqual(3)
  })

  test('with no kept entry, Accept is off and says why', { tag: '@IMP-42' }, async ({ page, api }) => {
    api.mock(importsApi.listIs([diary]), importsApi.daysAre(diary.id, [anImportDay('2024-03-01', 2, { rejected: true }), anImportDay('2024-03-02', 1, { rejectedCount: 1 })], 4), importsApi.entriesOfAnyRangeAre(diary.id, [coffee, walk, london]))
    const imports = importsPage(page)

    await imports.openReviewWithEntries(diary.id, 'Diary 2024')

    await expect(imports.at.accept).toBeDisabled()
    await expect(imports.at.accept).toHaveAccessibleDescription('Keep an entry to accept this batch, or discard it.')
  })

  test('the counts come from the days', { tag: '@IMP-41' }, async ({ page, api }) => {
    api.mock(
      importsApi.listIs([diary]),
      importsApi.daysAre(diary.id, [anImportDay('2024-03-01', 2, { rejectedCount: 1 }), anImportDay('2024-03-02', 1, { rejected: true }), anImportDay('2024-03-03', 0, { rejected: true })]),
      importsApi.entriesOfAnyRangeAre(diary.id, [coffee, walk, london]),
    )
    const imports = importsPage(page)

    await imports.openReviewWithEntries(diary.id, 'Diary 2024')

    await expect(imports.at.counts).toHaveText('3 entries: 1 kept, 2 rejected')
  })
})

test.describe('the accept of a batch', () => {
  test('Accept asks with the counts, sends the version of the days, and shows the result', { tag: ['@IMP-42', '@IMP-43'] }, async ({ page, api }) => {
    api.mock(importsApi.listIs([diary]), importsApi.daysAre(diary.id, [anImportDay('2024-03-01', 2, { rejectedCount: 1 }), anImportDay('2024-03-02', 1)], 9), importsApi.entriesOfAnyRangeAre(diary.id, [coffee, walk, london]))
    const accepted = anImportBatch(diary.id, diary.name, {
      state: 'Accepted',
      acceptedAt: '2026-11-25T10:00:00Z',
      entryCount: 3,
      result: { sessionsMade: 2, entriesRejected: 1, daysRejected: 0, skippedInJournal: 0, skippedDeleted: 0 },
    })
    api.mock(importsApi.accepts(accepted))
    const imports = importsPage(page)
    await imports.openReviewWithEntries(diary.id, 'Diary 2024')
    await imports.at.accept.click()
    await expect(imports.at.acceptDialog).toContainText('2 entries go into your journal, and 1 entry that you rejected stays out.')
    api.mock(importsApi.listIs([accepted]))

    await imports.at.acceptBatch.click()

    await expect(imports.at.result).toBeVisible()
    expect(api.last('post', importPaths.accept(diary.id))?.body).toEqual({ version: 9 })
    await expect(imports.at.resultLines).toHaveText([
      '2 entries made in your journal',
      '1 entry left out because you rejected it or its day',
      '0 days that you rejected',
      '0 entries skipped because your journal has them',
      '0 entries skipped because you deleted them from your journal',
    ])
    await expect(imports.at.toJournal).toHaveAttribute('href', '/')
    await expect(imports.at.accept).toHaveCount(0)
    await expect(imports.at.batchHeading('Diary 2024')).toBeFocused()
  })

  test('on a phone, Go to your journal is at least 44 px high', { tag: '@IMP-43' }, async ({ page, api }) => {
    await page.setViewportSize({ width: 390, height: 844 })
    api.mock(importsApi.listIs([anImportBatch(diary.id, diary.name, { state: 'Accepted', acceptedAt: '2026-11-25T10:00:00Z', result: { sessionsMade: 1, entriesRejected: 0, daysRejected: 0, skippedInJournal: 0, skippedDeleted: 0 } })]))
    const imports = importsPage(page)

    await imports.openBatch(diary.id, 'Diary 2024')

    expect(await imports.heightOf(imports.at.toJournal)).toBeGreaterThanOrEqual(44)
  })

  test('an accepted batch shows its result and no review action', { tag: '@IMP-43' }, async ({ page, api }) => {
    const accepted = anImportBatch(diary.id, diary.name, {
      state: 'Accepted',
      acceptedAt: '2026-11-25T10:00:00Z',
      result: { sessionsMade: 12, entriesRejected: 3, daysRejected: 1, skippedInJournal: 2, skippedDeleted: 1 },
    })
    api.mock(importsApi.listIs([accepted]))
    const imports = importsPage(page)

    await imports.openBatch(diary.id, 'Diary 2024')

    await expect(imports.at.resultLines).toHaveText([
      '12 entries made in your journal',
      '3 entries left out because you rejected them or their day',
      '1 day that you rejected',
      '2 entries skipped because your journal has them',
      '1 entry skipped because you deleted it from your journal',
    ])
    await expect(imports.at.discardOf('Diary 2024')).toHaveCount(0)
    await expect(imports.at.accept).toHaveCount(0)
  })
})

test.describe('the refusals of the Server', () => {
  test('a 409 of a changed batch keeps the dialog open and shows the new counts with one status line', { tag: '@IMP-44' }, async ({ page, api }) => {
    reviewOf(api)
    const changed = 'This batch changed after you read it. Read it again, then accept it.'
    api.mock(importsApi.refusesAccept(diary.id, { ...aProblem().staleVersion().build(), detail: changed, errors: { version: [changed] } }))
    const imports = importsPage(page)
    await imports.openReviewWithEntries(diary.id, 'Diary 2024')
    api.mock(importsApi.daysAre(diary.id, [...twoDays, anImportDay('2024-03-03', 1)], 7))

    await imports.accept(diary.id)

    await expect(imports.at.acceptReread).toHaveText('The batch changed. Recall read it again. Check the new counts, then accept the batch.')
    await expect(imports.at.acceptAlert).toHaveCount(0)
    await expect(imports.at.acceptDialog).toContainText('4 entries go into your journal')
    await expect(imports.at.result).toHaveCount(0)
  })

  test('a 404 of a removed entry shows the message, reads the batch again, and moves the focus to the heading', { tag: '@IMP-44' }, async ({ page, api }) => {
    reviewOf(api)
    api.mock(importsApi.refusesEntryReview(diary.id, aProblem().notFound('This entry is no longer in the batch. Review the other entries of the batch.').build()))
    const imports = importsPage(page)
    await imports.openReviewWithEntries(diary.id, 'Diary 2024')
    api.mock(importsApi.daysAre(diary.id, [anImportDay('2024-03-01', 1), anImportDay('2024-03-02', 1)], 5), importsApi.entriesOfAnyRangeAre(diary.id, [coffee, london]))

    await imports.reject(diary.id, '12:01 PM')

    await expect(imports.at.text('This entry is no longer in the batch. Review the other entries of the batch.')).toBeVisible()
    await expect(imports.at.entry('12:01 PM')).toHaveCount(0)
    await expect(imports.at.batchHeading('Diary 2024')).toBeFocused()
  })

  test('a 409 of a review of an accepted batch shows the message and the result', { tag: '@IMP-44' }, async ({ page, api }) => {
    reviewOf(api)
    api.mock(importsApi.refusesEntryReview(diary.id, aProblem().conflict('This batch is in your journal, so it cannot change.').build()))
    const imports = importsPage(page)
    await imports.openReviewWithEntries(diary.id, 'Diary 2024')
    api.mock(importsApi.listIs([{ ...diary, state: 'Accepted', acceptedAt: '2026-11-25T10:00:00Z', result: { sessionsMade: 3, entriesRejected: 0, daysRejected: 0, skippedInJournal: 0, skippedDeleted: 0 } }]))

    await imports.reject(diary.id, '8:30 AM')

    await expect(imports.at.text('This batch is in your journal, so it cannot change.')).toBeVisible()
    await expect(imports.at.result).toBeVisible()
  })

  test('a batch that does not exist says so, with a link to the imports', { tag: '@IMP-44' }, async ({ page, api }) => {
    api.mock(importsApi.listIs([diary]))
    const imports = importsPage(page)

    await page.goto('/imports/imp_9')

    await expect(imports.at.missingHeading).toBeVisible()
    await expect(imports.at.missing).toHaveText('This import batch does not exist. Pick a batch from your imports.')
    await imports.at.toImports.click()
    await expect(imports.at.heading).toBeVisible()
  })
})

test.describe('the missing batch on a phone', () => {
  test('Go to your imports is on its own line under the text, and at least 44 px high', { tag: '@IMP-44' }, async ({ page, api }) => {
    await page.setViewportSize({ width: 390, height: 844 })
    api.mock(importsApi.listIs([diary]))
    const imports = importsPage(page)

    await page.goto('/imports/imp_9')

    await expect(imports.at.missingHeading).toBeVisible()
    expect(await imports.heightOf(imports.at.toImports)).toBeGreaterThanOrEqual(44)
    expect(await imports.isBelow(imports.at.toImports, imports.at.missing)).toBe(true)
  })
})

test.describe('the import review setting', () => {
  test('says who can accept, and a change sends it with the version', { tag: '@IMP-45' }, async ({ page, api }) => {
    api.mock(importsApi.changesImportReview(aSetting({ requireImportReview: false, version: 2 })))
    const imports = importsPage(page)
    await imports.openSettings()
    await expect(imports.at.importReview).toBeChecked()

    await imports.at.importReviewLabel.click()

    await expect(imports.at.importReview).not.toBeChecked()
    expect(api.last('put', importPaths.importReview)?.body).toEqual({ requireImportReview: false, version: 1 })
  })

  test('a change from an old version reads the settings again and sends the change once more', { tag: '@IMP-45' }, async ({ page, api }) => {
    const imports = importsPage(page)
    await imports.openSettings()
    api.mock(
      settingsApi.are(aSetting({ version: 3 })),
      importsApi.changesImportReview(aSetting({ requireImportReview: false, version: 4 })),
      importsApi.refusesImportReview(aProblem().staleVersion().build()),
    )

    await imports.at.importReviewLabel.click()

    await expect(imports.at.importReview).not.toBeChecked()
    expect(api.all('put', importPaths.importReview).map((r) => r.body)).toEqual([
      { requireImportReview: false, version: 1 },
      { requireImportReview: false, version: 3 },
    ])
  })
})
