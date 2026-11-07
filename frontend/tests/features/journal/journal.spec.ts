import { aPerson, peopleApi, peoplePaths } from '@tests/support/api/people-api'
import { recordingsApi } from '@tests/support/api/recordings-api'
import { sessionsApi, sessionsPaths } from '@tests/support/api/sessions-api'
import { aProblem } from '@tests/support/builders/problem-details-builder'
import { aSession } from '@tests/support/builders/session-builder'
import { aSetting, settingsApi, settingsPaths } from '@tests/support/api/settings-api'
import { expect, test } from '@tests/support/fixtures'
import { entryPage, journalPage } from './journal.tasks'

const created = aSession().build()

const timesIn = (text: string, words: string) => text.split(words).length - 1

test.describe('writing a new entry', () => {
  test('tells the person when the jobs that turn audio into text stopped', async ({ page, api }) => {
    api.mock(recordingsApi.jobsStopped('The background work stopped. The Owner must restart the server.'))
    const journal = journalPage(page)

    await journal.open()

    await expect(journal.at.backgroundWorkNotice).toHaveText('The background work stopped. The Owner must restart the server.')
  })

  test('opens with the cursor in the composer, and asks for nothing first', async ({ page }) => {
    const journal = journalPage(page)

    await journal.open()

    await expect(journal.at.composer).toBeFocused()
    await expect(journal.at.emptyTimeline).toBeVisible()
  })

  test('a person who only opens the page makes no entry', async ({ page, api }) => {
    const journal = journalPage(page)

    await journal.open()

    await expect(journal.at.done).toBeDisabled()
    expect(api.all('post', sessionsPaths.timeline)).toHaveLength(0)
  })

  test('the first words make one entry with a client ID and a start time, then save the words', async ({ page, api }) => {
    const isNew = aSession().atRevision(0).saying('').build()
    api.mock(sessionsApi.creates(isNew), sessionsApi.draftSaveWins(isNew, 1))
    const journal = journalPage(page)
    await journal.open()

    await journal.write('Morning coffee')

    await expect(journal.at.status('Saved at')).toBeVisible()
    await expect(journal.at.conflict('safe in History')).toHaveCount(0)
    expect(api.all('post', sessionsPaths.timeline)).toHaveLength(1)
    const create = api.last('post', sessionsPaths.timeline)?.body as { clientSessionId: string; startedAt: string }
    expect(create.clientSessionId).toMatch(/^[0-9a-f-]{36}$/)
    expect(new Date(create.startedAt).getTime()).not.toBeNaN()
    const save = api.last('put', sessionsPaths.rawDraft(isNew.id))?.body as { text: string; baseRevision: number }
    expect(save.text).toContain('Morning coffee')
    expect(save.baseRevision).toBe(isNew.rawLatestRevision)
  })

  test('the entry stays out of the list until Done, then the composer is empty for the next one', async ({ page, api }) => {
    const summary = aSession().summary('Morning coffee')
    api.mock(sessionsApi.creates({ ...created, id: summary.id }), {
      method: 'put',
      path: sessionsPaths.rawDraft(summary.id),
      status: 200,
      body: { outcome: 'Won', draftRevision: 2, latestRevision: 2, savedAt: '2026-11-06T15:31:00+00:00', cleanupStatus: 'NotRun', version: 2 },
    })
    const journal = journalPage(page)
    await journal.open()
    api.mock(sessionsApi.timelineIs([summary]))

    await journal.write('Morning coffee')
    await expect(journal.at.entryLink('Morning coffee')).toHaveCount(0)
    await journal.finish()

    await expect(journal.at.entryLink('Morning coffee')).toBeVisible()
    await expect(journal.at.composer).toHaveText('')
    await expect(journal.at.composer).toBeFocused()
  })

  test('the keyboard finishes an entry too', async ({ page, api }) => {
    api.mock(sessionsApi.creates(created), sessionsApi.draftSaveWins(created, 2))
    const journal = journalPage(page)
    await journal.open()
    await journal.write('Words')

    await journal.finishFromTheKeyboard()

    await expect(journal.at.composer).toHaveText('')
  })

  test('a reload goes back to the entry, with its words and with no second entry', async ({ page, api }) => {
    const saved = aSession().saying('Morning coffee').build()
    api.mock(sessionsApi.creates(saved), sessionsApi.draftSaveWins(saved, 2), sessionsApi.oneIs(saved))
    const journal = journalPage(page)
    await journal.open()
    await journal.write('Morning coffee')
    await expect(journal.at.status('Saved at')).toBeVisible()

    await journal.reload()

    await expect(journal.at.composer).toContainText('Morning coffee')
    expect(api.all('post', sessionsPaths.timeline)).toHaveLength(1)
  })

  test('a reload before the reply sends the words that had no reply, with their own time', async ({ page, api }) => {
    api.mock(sessionsApi.creates(created), sessionsApi.draftSaveHasNoReply(created))
    const journal = journalPage(page)
    await journal.open()
    await journal.write('Words with no reply')
    await expect(journal.at.status('Not saved yet')).toBeVisible()
    const first = api.last('put', sessionsPaths.rawDraft(created.id))?.body as { savedAt: string }

    api.mock(sessionsApi.draftSaveWins(created, 2))
    await page.reload()

    await expect(journal.at.composer).toContainText('Words with no reply')
    await expect.poll(() => (api.last('put', sessionsPaths.rawDraft(created.id))?.body as { savedAt: string }).savedAt).toBe(first.savedAt)
    await expect(journal.at.status('Saved at')).toBeVisible()
  })

  test('a resume that cannot read its entry starts over once, and the next words make one entry', async ({ page, api }) => {
    const gone = aSession().saying('Old words').build()
    api.mock(sessionsApi.creates(gone), sessionsApi.draftSaveWins(gone, 2), sessionsApi.oneIs(gone))
    const journal = journalPage(page)
    await journal.open()
    await journal.write('Old words')
    await expect(journal.at.status('Saved at')).toBeVisible()

    const next = aSession().build()
    api.mock(sessionsApi.oneFails(gone.id, aProblem().notFound('The entry is gone.')), sessionsApi.creates(next), sessionsApi.draftSaveWins(next, 2))
    await journal.reload()
    await expect(journal.at.composer).toBeEmpty()
    await journal.write('Hello there')

    await expect(journal.at.composer).toHaveText('Hello there')
    expect(api.all('post', sessionsPaths.timeline)).toHaveLength(2)
  })

  test('a save whose words left the Draft says so, and Add my words joins them to the newest words with the cursor after them', async ({ page, api }) => {
    api.mock(sessionsApi.creates(created), sessionsApi.oneIs(created), sessionsApi.draftSaveIsKept(created, { draftRevision: 2, latestRevision: 3 }))
    const journal = journalPage(page)
    await journal.open()
    await journal.write('My words')
    await expect(journal.at.conflict('The words on this page are safe in History.')).toBeVisible()
    await expect(journal.at.status('Saved in History at')).toBeVisible()

    const newest = { ...aSession().atRevision(3).saying('From the other tab').build(), id: created.id, clientSessionId: created.clientSessionId }
    api.mock(sessionsApi.oneIs(newest), sessionsApi.draftSaveWins(created, 5))
    await journal.at.addMyWords.click()

    await expect(journal.at.status('Saved at')).toBeVisible()
    await expect(journal.at.conflict('The words on this page are safe in History.')).toHaveCount(0)
    await expect(journal.at.composer).toContainText('From the other tab')
    await expect(journal.at.composer).toContainText('My words')
    await expect(journal.at.composer).toBeFocused()
    await journal.typeAfterTheCursor('X')
    await expect(journal.at.composer).toContainText('My wordsX')
  })

  test('a save whose words left the Draft can show the newest words, and the next save bases on them', async ({ page, api }) => {
    api.mock(sessionsApi.creates(created), sessionsApi.oneIs(created), sessionsApi.draftSaveIsKept(created, { draftRevision: 2, latestRevision: 3 }))
    const journal = journalPage(page)
    await journal.open()
    await journal.write('My words')

    const newest = { ...aSession().atRevision(3).saying('From the other tab').build(), id: created.id, clientSessionId: created.clientSessionId }
    api.mock(sessionsApi.oneIs(newest), sessionsApi.draftSaveWins(created, 5))
    await journal.at.showNewest.click()

    await expect(journal.at.composer).toHaveText('From the other tab')
    await expect(journal.at.composer).toBeFocused()
    await journal.write(' and more')
    expect((api.last('put', sessionsPaths.rawDraft(created.id))?.body as { baseRevision: number }).baseRevision).toBe(3)
  })

  test('an entry that another tab deleted starts again as a new entry with all the words on the page', async ({ page, api }) => {
    api.mock(sessionsApi.creates(created), { ...sessionsApi.draftSaveIsRefused(created, aProblem().notFound('The entry is gone.')), delayMs: 1000 })
    const journal = journalPage(page)
    await journal.open()
    await journal.typeWithoutWaiting('Words for a deleted entry')
    await expect.poll(() => api.all('put', sessionsPaths.rawDraft(created.id)).length).toBe(1)

    const next = aSession().build()
    api.mock(sessionsApi.creates(next), sessionsApi.draftSaveWins(next, 1))

    await expect(journal.at.status('Saved at')).toBeVisible()
    await expect(journal.at.composer).toHaveText('Words for a deleted entry')
    expect(JSON.stringify(api.last('put', sessionsPaths.rawDraft(next.id))?.body)).toContain('Words for a deleted entry')
    const [first, second] = api.all('post', sessionsPaths.timeline).map((r) => (r.body as { clientSessionId: string }).clientSessionId)
    expect(second).not.toBe(first)
  })

  test('a create that the Server refuses for a deleted entry starts again as a new entry', async ({ page, api }) => {
    api.mock({ ...sessionsApi.createIsRefused(aProblem().conflict('This Session was deleted, so it cannot come back. Drop the local copy.')), delayMs: 1000 })
    const journal = journalPage(page)
    await journal.open()
    await journal.typeWithoutWaiting('Words for a deleted entry')
    await expect.poll(() => api.all('post', sessionsPaths.timeline).length).toBe(1)

    const next = aSession().build()
    api.mock(sessionsApi.creates(next), sessionsApi.draftSaveWins(next, 1))

    await expect(journal.at.status('Saved at')).toBeVisible()
    await expect(journal.at.composer).toHaveText('Words for a deleted entry')
  })

  test('a person who leaves just after the first words finds them on the way back', async ({ page, api }) => {
    const saved = { ...aSession().saying('Words before a move').build(), id: created.id, clientSessionId: created.clientSessionId }
    api.mock({ ...sessionsApi.creates(created), delayMs: 300 }, sessionsApi.draftSaveWins(created, 1), sessionsApi.oneIs(saved))
    const journal = journalPage(page)
    await journal.open()
    // The create reads the timeline, and that read stays fresh until the return. It must already hold the row.
    api.mock(sessionsApi.timelineIs([{ ...aSession().summary('Words before a move'), id: created.id }]))
    await journal.typeWithoutWaiting('Words before a move')
    await journal.goToSettings()
    await expect.poll(() => api.all('put', sessionsPaths.rawDraft(created.id)).length).toBe(1)

    await journal.goToTheJournalFromTheSidebar()

    await expect(journal.at.composer).toHaveText('Words before a move')
    await journal.finish()
    await expect(journal.at.entryLink('Words before a move')).toBeVisible()
  })

  test('a person who leaves just after the first words and reloads finds them on the way back', async ({ page, api }) => {
    api.mock({ ...sessionsApi.creates(created), delayMs: 300 }, sessionsApi.draftSaveWins(created, 1))
    const journal = journalPage(page)
    await journal.open()
    await journal.typeWithoutWaiting('Words before a reload')
    await journal.goToSettings()
    await expect.poll(() => api.all('put', sessionsPaths.rawDraft(created.id)).length).toBe(1)
    await journal.letAnAnswerHeldForHalfASecondLand()

    const saved = { ...aSession().saying('Words before a reload').build(), id: created.id, clientSessionId: created.clientSessionId }
    api.mock(sessionsApi.oneIs(saved), sessionsApi.timelineIs([{ ...aSession().summary('Words before a reload'), id: created.id }]))
    await journal.reloadOnSettings()
    await journal.goToTheJournalFromTheSidebar()

    await expect(journal.at.composer).toHaveText('Words before a reload')
  })

  test('a save over words that the create found on the Server loses, then joins them and saves from the read', async ({ page, api }) => {
    const replayed = aSession().atRevision(1).saying('Words from another tab').build()
    api.mock(sessionsApi.creates(replayed), { ...sessionsApi.draftSaveLoses(replayed, { draftRevision: 1, latestRevision: 2 }), delayMs: 300 }, sessionsApi.oneIs(replayed))
    const journal = journalPage(page)
    await journal.open()

    await journal.typeWithoutWaiting('Words from this tab')
    await expect.poll(() => api.all('put', sessionsPaths.rawDraft(replayed.id)).length).toBe(1)
    api.mock(sessionsApi.draftSaveWins(replayed, 2))

    await expect(journal.at.mergedNotice).toBeVisible()
    await expect(journal.at.status('Saved at')).toBeVisible()
    const [lost, merged] = api.all('put', sessionsPaths.rawDraft(replayed.id)).map(({ body }) => body as { baseRevision: number; text: string; savedAt: string })
    expect(lost.baseRevision).toBe(0)
    expect(merged.baseRevision).toBe(1)
    expect(merged.savedAt).not.toBe(lost.savedAt)
    expect(timesIn(merged.text, 'Words from this tab')).toBe(1)
    expect(timesIn(merged.text, 'Words from another tab')).toBe(1)
    await expect(journal.at.composer).toContainText('Words from this tab')
    await expect(journal.at.composer).toContainText('Words from another tab')
    expect(await journal.at.conflict('safe in History').count()).toBe(0)
  })

  test('a return to the journal with a read that fails opens the last known words and keeps the entry', async ({ page, api }) => {
    const saved = aSession().saying('Morning coffee').build()
    api.mock(sessionsApi.creates(saved), sessionsApi.draftSaveWins(saved, 2), sessionsApi.oneIs(saved))
    const journal = journalPage(page)
    await journal.open()
    await journal.write('Morning coffee')
    await expect(journal.at.status('Saved at')).toBeVisible()
    await journal.goToSettings()
    await journal.goToTheJournalFromTheSidebar()
    await expect(journal.at.composer).toHaveText('Morning coffee')

    await journal.goToSettings()
    api.mock(sessionsApi.oneFails(saved.id, aProblem().refusing('Not now.')))
    await journal.goToTheJournalFromTheSidebar()

    await expect(journal.at.composer).toHaveText('Morning coffee')
    await expect(journal.at.done).toBeEnabled()
  })

  test('a return to the journal after a delete elsewhere finds the words in a new entry', async ({ page, api }) => {
    api.mock(sessionsApi.creates(created), sessionsApi.oneIs(created), sessionsApi.draftSaveWins(created, 1))
    const journal = journalPage(page)
    await journal.open()
    await journal.write('First words')
    await expect(journal.at.status('Saved at')).toBeVisible()

    api.mock(sessionsApi.draftSaveIsRefused(created, aProblem().notFound('The entry is gone.')), sessionsApi.oneFails(created.id, aProblem().notFound('The entry is gone.')))
    await journal.typeWithoutWaiting(' and more')
    const next = aSession().build()
    const savedNext = { ...aSession().saying('First words and more').build(), id: next.id, clientSessionId: next.clientSessionId }
    api.mock(sessionsApi.creates(next), sessionsApi.draftSaveWins(next, 1), sessionsApi.oneIs(savedNext))
    await journal.goToSettings()
    await expect(journal.at.movedNotice).toBeVisible()
    await expect.poll(() => JSON.stringify(api.last('put', sessionsPaths.rawDraft(next.id))?.body)).toContain('First words and more')
    await journal.goToTheJournalFromTheSidebar()

    await expect(journal.at.composer).toHaveText('First words and more')
    await expect(journal.at.movedNotice).toBeVisible()
    expect(api.all('post', sessionsPaths.timeline)).toHaveLength(2)
  })

  test('a conflict from before a return still shows on the way back, with the words of the person', async ({ page, api }) => {
    api.mock(sessionsApi.creates(created), sessionsApi.oneIs(created), sessionsApi.draftSaveIsKept(created, { draftRevision: 2, latestRevision: 3 }))
    const journal = journalPage(page)
    await journal.open()
    await journal.write('My words')
    await expect(journal.at.addMyWords).toBeVisible()
    await journal.goToSettings()

    const newest = { ...aSession().atRevision(3).saying('From the other tab').build(), id: created.id, clientSessionId: created.clientSessionId }
    api.mock({ ...sessionsApi.oneIs(newest), delayMs: 800 })
    await journal.goToTheJournalFromTheSidebar()

    await expect(journal.at.composer).toHaveText('My words')
    await expect(journal.at.conflict('The words on this page are safe in History.')).toBeVisible()
    await journal.at.showNewest.click()
    await expect(journal.at.composer).toHaveText('From the other tab')
    await expect(journal.at.addMyWords).toHaveCount(0)
  })

  test('a return to the journal with no network opens the last known words, and a person can write', async ({ page, api }) => {
    const saved = aSession().saying('Morning coffee').build()
    api.mock(sessionsApi.creates(saved), sessionsApi.draftSaveWins(saved, 2), sessionsApi.oneIs(saved))
    const journal = journalPage(page)
    await journal.open()
    await journal.write('Morning coffee')
    await expect(journal.at.status('Saved at')).toBeVisible()
    await journal.goToSettings()

    await page.context().setOffline(true)
    await journal.goToTheJournalFromTheSidebar()

    await expect(journal.at.composer).toHaveText('Morning coffee')
    await page.context().setOffline(false)
  })

  test('a return with no network after the last read has expired opens the saved words, and a person can write', async ({ page, api }) => {
    const saved = aSession().saying('Morning coffee').build()
    api.mock(sessionsApi.creates(saved), sessionsApi.draftSaveWins(saved, 2), sessionsApi.oneIs(saved))
    const journal = journalPage(page)
    await page.clock.install()
    await journal.open()
    await journal.write('Morning coffee')
    await expect(journal.at.status('Saved at')).toBeVisible()
    await journal.goToSettings()

    await page.context().setOffline(true)
    await page.clock.fastForward('06:00')
    await journal.goToTheJournalFromTheSidebar()

    await expect(journal.at.composer).toHaveText('Morning coffee')
    await journal.typeWithoutWaiting(' with no network')
    await expect(journal.at.composer).toContainText('with no network')
    await page.context().setOffline(false)
  })

  test('a return with a read that hangs opens the saved words after a short wait', async ({ page, api }) => {
    const saved = aSession().saying('Morning coffee').build()
    api.mock(sessionsApi.creates(saved), sessionsApi.draftSaveWins(saved, 2), sessionsApi.oneIs(saved))
    const journal = journalPage(page)
    await journal.open()
    await journal.write('Morning coffee')
    await expect(journal.at.status('Saved at')).toBeVisible()
    await journal.goToSettings()

    api.mock({ ...sessionsApi.oneIs(saved), delayMs: 20_000 })
    await journal.goToTheJournalFromTheSidebar()

    await expect(journal.at.composer).toHaveText('Morning coffee', { timeout: 8_000 })
    await expect(journal.at.done).toBeEnabled()
  })

  test('Done during a read of the newest words leaves the next entry empty', async ({ page, api }) => {
    api.mock(sessionsApi.creates(created), sessionsApi.oneIs(created), sessionsApi.draftSaveIsKept(created, { draftRevision: 2, latestRevision: 3 }))
    const journal = journalPage(page)
    await journal.open()
    await journal.write('My words')
    await expect(journal.at.showNewest).toBeVisible()

    const newest = { ...aSession().atRevision(3).saying('From the other tab').build(), id: created.id, clientSessionId: created.clientSessionId }
    api.mock({ ...sessionsApi.oneIs(newest), delayMs: 300 })
    await journal.at.showNewest.click()
    await journal.finish()
    await journal.letAnAnswerHeldForHalfASecondLand()

    await expect(journal.at.composer).toBeEmpty()
  })

  test('a mention names a person from the directory, and a save carries the ID of the person', async ({ page, api }) => {
    api.mock(peopleApi.listIs([aPerson('per_1', 'Ana Reyes')]), sessionsApi.creates(created), sessionsApi.draftSaveWins(created, 2))
    const journal = journalPage(page)
    await journal.open()
    await expect.poll(() => api.all('get', peoplePaths.list).length).toBeGreaterThan(0)

    await journal.typeWithoutWaiting('Lunch with @Ana')
    await expect(journal.at.mentionOption('Ana Reyes')).toBeVisible()
    await journal.pickFromTheMentionList()

    await expect(journal.at.mentionInEditor('Ana Reyes')).toBeVisible()
    await expect.poll(() => JSON.stringify(api.last('put', sessionsPaths.rawDraft(created.id))?.body)).toContain('per_1')
  })

  test('a mention shows as a tag on the row after Done, with no new read of the list', async ({ page, api }) => {
    api.mock(
      peopleApi.listIs([aPerson('per_1', 'Ana Reyes')]),
      sessionsApi.creates(created),
      sessionsApi.draftSaveWins(created, 2),
      sessionsApi.timelineIs([{ ...aSession().summary('Lunch with'), id: created.id }]),
    )
    const journal = journalPage(page)
    await journal.open()
    await expect.poll(() => api.all('get', peoplePaths.list).length).toBeGreaterThan(0)

    await journal.write('Lunch with')
    await journal.typeWithoutWaiting(' @Ana')
    await expect(journal.at.mentionOption('Ana Reyes')).toBeVisible()
    await journal.pickFromTheMentionList()
    await expect.poll(() => JSON.stringify(api.last('put', sessionsPaths.rawDraft(created.id))?.body)).toContain('per_1')
    await expect(journal.at.status('Saved at')).toBeVisible()
    await journal.finish()

    await expect(journal.at.tag('@Ana Reyes')).toBeVisible()
  })

  test('a mention of a new name adds the person first', async ({ page, api }) => {
    api.mock(peopleApi.adds(aPerson('per_9', 'Zed')), sessionsApi.creates(created), sessionsApi.draftSaveWins(created, 2))
    const journal = journalPage(page)
    await journal.open()

    await journal.typeWithoutWaiting('Saw @Zed')
    await expect(journal.at.mentionOption('Add Zed to your people')).toBeVisible()
    await journal.pickFromTheMentionList()

    await expect(journal.at.mentionInEditor('Zed')).toBeVisible()
    expect(api.last('post', peoplePaths.list)?.body).toEqual({ label: 'Zed' })
  })
})

test.describe('the time zone of a new user', () => {
  test('sets the zone of the browser once, so that the days are right', async ({ page, api }) => {
    api.mock(settingsApi.are(aSetting({ timeZone: null })), settingsApi.saves(aSetting()))
    const journal = journalPage(page)

    await journal.open()

    await expect.poll(() => api.all('put', settingsPaths.mine)).toHaveLength(1)
    expect((api.last('put', settingsPaths.mine)?.body as { timeZone: string }).timeZone).toBeTruthy()
  })
})

test.describe('a row of the journal', () => {
  test('a preview that the Server cut ends at a word and shows the cut, and reads in its own direction', async ({ page, api }) => {
    const journal = journalPage(page)
    api.mock(sessionsApi.timelineIs([aSession().summary(`${'word '.repeat(39)}inter`)]))

    await journal.open()

    const row = journal.at.previewOfARow(`${'word '.repeat(38)}word\u2026`)
    await expect(row).toBeVisible()
    await expect(row).toHaveAttribute('dir', 'auto')
  })
})

test.describe('the timeline', () => {
  test('a mood and a topic with one name show one tag', async ({ page, api }) => {
    api.mock(sessionsApi.timelineIs([aSession().tagged({ moods: ['Calm'], topics: ['Calm'] }).summary('A quiet day')]))
    const journal = journalPage(page)

    await journal.open()

    await expect(journal.at.entryLink('A quiet day')).toBeVisible()
    await expect(journal.at.tag('Calm')).toHaveCount(1)
  })

  test('groups rows by day and names the day, with the tags of each entry', async ({ page, api }) => {
    const today = new Date().toISOString().slice(0, 10)
    const rows = [
      aSession().onDay(today).tagged({ activity: 'Walking', moods: ['Calm'], topics: ['work'] }).mentioning('per_1').summary('A walk at noon'),
      aSession().onDay('2026-01-02').summary('An old entry'),
    ]
    api.mock(sessionsApi.timelineIs(rows), peopleApi.listIs([aPerson('per_1', 'Ana Reyes')]))
    const journal = journalPage(page)

    await journal.open()

    await expect(journal.at.entryLink('A walk at noon')).toBeVisible()
    await expect(journal.at.tag('Walking')).toBeVisible()
    await expect(journal.at.tag('Calm')).toBeVisible()
    await expect(journal.at.tag('work')).toBeVisible()
    await expect(journal.at.tag('@Ana Reyes')).toBeVisible()
    await expect(journal.at.entryLink('An old entry')).toBeVisible()
  })

  test('an entry whose first reply was lost opens with its words', async ({ page, api }) => {
    const session = aSession().atRevision(0).saying('').build()
    const row = { ...aSession().summary('Words that reached the server'), id: session.id }
    const onTheServer = { ...aSession().atRevision(1).saying('Words that reached the server').build(), id: session.id, clientSessionId: session.clientSessionId }
    api.mock(sessionsApi.creates(session), sessionsApi.oneIs(onTheServer), sessionsApi.draftSaveIsUnchanged(session, { draftRevision: 1, latestRevision: 1 }), sessionsApi.timelineIs([row]))
    const journal = journalPage(page)
    await journal.open()
    await journal.write('Words that reached the server')
    await journal.finish()

    await journal.at.entryLink('Words that reached the server').click()

    await expect(entryPage(page).at.words('Words that reached the server')).toBeVisible()
  })

  test('with a refused save, Done stays off, so the words on the page do not go', async ({ page, api }) => {
    const session = aSession().build()
    api.mock(sessionsApi.creates(session), sessionsApi.draftSaveIsRefused(session, aProblem().refusing('This text is too long.')))
    const journal = journalPage(page)
    await journal.open()

    await journal.typeWithoutWaiting('Too many words')

    await expect(journal.at.status('Not saved. This text is too long.')).toBeVisible()
    await expect(journal.at.done).toBeDisabled()
  })

  test('a refused long text leaves the composer, the reason, and Done on the screen', async ({ page, api }) => {
    const session = aSession().build()
    const rows = Array.from({ length: 30 }, (_, row) => ({ ...aSession().summary(`Older words ${row}`), id: `ses_${row}` }))
    api.mock(sessionsApi.timelineIs(rows), sessionsApi.creates(session), sessionsApi.draftSaveIsRefused(session, aProblem().refusing('This text is too long.')))
    const journal = journalPage(page)
    await journal.open()
    await journal.at.composer.click()

    await page.keyboard.insertText(Array.from({ length: 200 }, (_, line) => `line ${line}`).join('\n'))

    await expect(journal.at.status('Not saved. This text is too long.')).toBeInViewport()
    await expect(journal.at.composer).toBeInViewport()
    await expect(journal.at.done).toBeInViewport()
  })

  test('a refusal that answers after Done brings the words back, and Done stays off', async ({ page, api }) => {
    const session = aSession().build()
    api.mock(sessionsApi.creates(session), { ...sessionsApi.draftSaveIsRefused(session, aProblem().refusing('This text is too long.')), delayMs: 300 })
    const journal = journalPage(page)
    await journal.open()

    await journal.typeWithoutWaiting('Too many words')
    await journal.finish()

    await expect(journal.at.status('Not saved. This text is too long.')).toBeVisible()
    await expect(journal.at.composer).toHaveText('Too many words')
    await expect(journal.at.done).toBeDisabled()
  })

  test('a delete that answers after Done keeps the words in a new entry and says so', async ({ page, api }) => {
    const session = aSession().build()
    api.mock(sessionsApi.creates(session), sessionsApi.draftSaveWins(session, 1))
    const journal = journalPage(page)
    await journal.open()
    await journal.write('Words for a deleted entry')
    await expect(journal.at.status('Saved at')).toBeVisible()

    api.mock({ ...sessionsApi.draftSaveIsRefused(session, aProblem().notFound('This Session does not exist.')), delayMs: 300 })
    await journal.typeWithoutWaiting(' and more')
    await journal.finish()
    const next = aSession().build()
    api.mock(sessionsApi.creates(next), sessionsApi.draftSaveWins(next, 1))

    await expect.poll(() => JSON.stringify(api.last('put', sessionsPaths.rawDraft(next.id))?.body)).toContain('Words for a deleted entry and more')
    await expect(journal.at.movedNotice).toBeVisible()
    await expect(journal.at.composer).toBeEmpty()
  })

  test('a save whose words left the Draft after Done brings the words back with the notice', async ({ page, api }) => {
    const session = aSession().build()
    api.mock(sessionsApi.creates(session), sessionsApi.draftSaveWins(session, 1))
    const journal = journalPage(page)
    await journal.open()
    await journal.write('Words on the phone')
    await expect(journal.at.status('Saved at')).toBeVisible()

    api.mock({ ...sessionsApi.draftSaveIsKept(session, { draftRevision: 2, latestRevision: 3 }), delayMs: 300 }, sessionsApi.oneIs(session))
    await journal.typeWithoutWaiting(' and more')
    await journal.finish()

    await expect(journal.at.conflict('The words on this page are safe in History.')).toBeVisible()
    await expect(journal.at.composer).toHaveText('Words on the phone and more')
  })

  test('a save that loses after Done merges the words into the newest words and saves them, and the next entry stays empty', async ({ page, api }) => {
    const session = aSession().build()
    api.mock(sessionsApi.creates(session), sessionsApi.draftSaveWins(session, 1))
    const journal = journalPage(page)
    await journal.open()
    await journal.write('Words on the phone')
    await expect(journal.at.status('Saved at')).toBeVisible()

    api.mock({ ...sessionsApi.draftSaveLoses(session, { draftRevision: 2, latestRevision: 3 }), delayMs: 300 })
    await journal.typeWithoutWaiting(' and more')
    await journal.finish()
    await expect.poll(() => api.all('put', sessionsPaths.rawDraft(session.id)).length).toBe(2)
    const newest = { ...aSession().atRevision(3).saying('Words on the phone', 'From the other device').build(), id: session.id, clientSessionId: session.clientSessionId }
    api.mock(sessionsApi.oneIs(newest), sessionsApi.draftSaveWins(session, 4))

    await expect.poll(() => api.all('put', sessionsPaths.rawDraft(session.id)).length).toBe(3)
    const merged = api.last('put', sessionsPaths.rawDraft(session.id))?.body as { baseRevision: number; text: string }
    expect(merged.baseRevision).toBe(3)
    expect(timesIn(merged.text, 'Words on the phone and more')).toBe(1)
    expect(timesIn(merged.text, 'From the other device')).toBe(1)
    await expect(journal.at.composer).toBeEmpty()
    expect(await journal.at.conflict('safe in History').count()).toBe(0)
  })

  test('a refusal after Done keeps the entry, so a later save and a reload open its words', async ({ page, api }) => {
    const session = aSession().build()
    api.mock(
      { ...sessionsApi.creates(session), delayMs: 500 },
      sessionsApi.draftSaveIsRefused(session, aProblem().refusing('This text is too long.')),
    )
    const journal = journalPage(page)
    await journal.open()
    await journal.typeWithoutWaiting('Too many words')
    await journal.finish()
    await expect(journal.at.status('Not saved. This text is too long.')).toBeVisible()

    api.mock(sessionsApi.draftSaveWins(session, 1))
    await journal.typeWithoutWaiting(' cut')
    await expect(journal.at.status('Saved at')).toBeVisible()
    api.mock(sessionsApi.oneIs({ ...aSession().saying('Too many words cut').build(), id: session.id, clientSessionId: session.clientSessionId }))
    await journal.reload()

    await expect(journal.at.composer).toHaveText('Too many words cut')
  })

  test('reads the next page with the cursor when the person scrolls to the end', async ({ page, api }) => {
    const firstPage = Array.from({ length: 40 }, (_, index) => aSession().summary(`Entry number ${index}`))
    api.mock(sessionsApi.timelineIs(firstPage, 'next-page'))
    const journal = journalPage(page)
    await journal.open()
    await expect(journal.at.loadedEntries).toHaveCount(40)

    await journal.scrollToTheEnd()

    await expect.poll(() => api.last('get', sessionsPaths.timeline)?.query.get('cursor')).toBe('next-page')
  })

  test('a next page that fails offers Retry, and does not ask again by itself', async ({ page, api }) => {
    const firstPage = Array.from({ length: 40 }, (_, index) => aSession().summary(`Entry number ${index}`))
    api.mock(sessionsApi.timelineIs(firstPage, 'next-page'))
    const journal = journalPage(page)
    await journal.open()
    await expect(journal.at.loadedEntries).toHaveCount(40)
    api.mock(sessionsApi.timelineFails(aProblem().forbidden('You cannot read this journal.')))

    await journal.scrollToTheEnd()
    await expect(journal.at.moreEntriesFailure).toBeVisible()
    await journal.scrollToTheEnd()

    const asked = () => api.all('get', sessionsPaths.timeline).filter((request) => request.query.get('cursor') === 'next-page').length
    await journal.waitForAPageOfFrames()
    expect(asked()).toBe(1)
  })

  test('shows the refusal of the Server once, in place of the list', async ({ page, api }) => {
    api.mock(sessionsApi.timelineFails(aProblem().forbidden('You cannot read this journal.')))
    const journal = journalPage(page)

    await journal.open()

    await expect(journal.at.timelineFailure).toContainText('You cannot read this journal.')
    expect(api.all('get', sessionsPaths.timeline)).toHaveLength(1)
  })
})

test.describe('the journal on a phone', () => {
  test('keeps the line with the cursor above the status and Done in a long text', async ({ page, api }) => {
    const session = aSession().build()
    api.mock(sessionsApi.creates(session), sessionsApi.draftSaveWins(session, 1))
    const journal = journalPage(page)
    await page.setViewportSize({ width: 390, height: 500 })
    await journal.open()
    await journal.at.composer.click()

    for (let line = 0; line < 30; line++) {
      await page.keyboard.insertText(`line ${line}`)
      await page.keyboard.press('Enter')
    }
    await page.keyboard.insertText('the last line')

    expect((await journal.cursorLineBottom()) ?? Infinity).toBeLessThanOrEqual((await journal.at.done.boundingBox())!.y)
  })

  test.use({ viewport: { width: 390, height: 844 } })

  test('has no sideways scroll with a long word', async ({ page, api }) => {
    const longWord = 'Maximilianbartholomewalexandervanderwesthuizenmontgomerysmythesinclairfitzgeraldworthington'
    api.mock(sessionsApi.timelineIs([aSession().tagged({ topics: [longWord] }).summary(longWord)]))
    const journal = journalPage(page)

    await journal.open()

    await expect(journal.at.entryLink(longWord)).toBeVisible()
    expect(await journal.pageSideScrollRange()).toBe(0)
  })

  test('a save that loses to another device merges the words of the person into the newest words, saves them, and says so', async ({ page, api }) => {
    const session = aSession().build()
    api.mock(sessionsApi.creates(session), sessionsApi.draftSaveWins(session, 1))
    const journal = journalPage(page)
    await journal.open()
    await journal.write('My words')
    await expect(journal.at.status('Saved at')).toBeVisible()

    api.mock({ ...sessionsApi.draftSaveLoses(session, { draftRevision: 2, latestRevision: 3 }), delayMs: 300 })
    await journal.typeWithoutWaiting(' and more')
    await expect.poll(() => api.all('put', sessionsPaths.rawDraft(session.id)).length).toBe(2)
    const newest = { ...aSession().atRevision(3).saying('My words', 'From the other device').build(), id: session.id, clientSessionId: session.clientSessionId }
    api.mock(sessionsApi.oneIs(newest), sessionsApi.draftSaveWins(session, 4))

    await expect(journal.at.mergedNotice).toBeVisible()
    await expect(journal.at.status('Saved at')).toBeVisible()
    expect(api.all('get', sessionsPaths.one(session.id)).length).toBeGreaterThan(0)
    const [, lost, merged, ...more] = api.all('put', sessionsPaths.rawDraft(session.id)).map(({ body }) => body as { baseRevision: number; text: string; savedAt: string })
    expect(more).toEqual([])
    expect(merged.baseRevision).toBe(3)
    expect(merged.savedAt).not.toBe(lost.savedAt)
    expect(timesIn(merged.text, 'My words')).toBe(1)
    expect(timesIn(merged.text, 'My words and more')).toBe(1)
    expect(timesIn(merged.text, 'From the other device')).toBe(1)
    await expect(journal.at.composer).toContainText('My words and more')
    await expect(journal.at.composer).toContainText('From the other device')
    expect(await journal.at.conflict('safe in History').count()).toBe(0)
    await expect(journal.at.mergedNotice).toBeInViewport({ ratio: 1 })
    await expect(journal.at.dismissMerged).toBeInViewport({ ratio: 1 })
    expect(await journal.pageSideScrollRange()).toBe(0)

    await journal.at.dismissMerged.click()

    await expect(journal.at.mergedNotice).toHaveCount(0)
  })

  test('reaches Logout from the sidebar sheet', async ({ page }) => {
    const journal = journalPage(page)
    await journal.open()

    await journal.at.sidebarToggle.click()

    await expect(journal.at.userMenu).toBeVisible()
  })
})
