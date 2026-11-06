import { aPerson, peopleApi, peoplePaths } from '@tests/support/api/people-api'
import { sessionsApi, sessionsPaths } from '@tests/support/api/sessions-api'
import { aProblem } from '@tests/support/builders/problem-details-builder'
import { aSession } from '@tests/support/builders/session-builder'
import { aSetting, settingsApi, settingsPaths } from '@tests/support/api/settings-api'
import { expect, test } from '@tests/support/fixtures'
import { entryPage, journalPage } from './journal.tasks'

const created = aSession().build()

test.describe('writing a new entry', () => {
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
    api.mock(sessionsApi.creates(created), sessionsApi.draftSaveWins(created, 2))
    const journal = journalPage(page)
    await journal.open()

    await journal.write('Morning coffee')

    await expect(journal.at.status('Saved at')).toBeVisible()
    expect(api.all('post', sessionsPaths.timeline)).toHaveLength(1)
    const create = api.last('post', sessionsPaths.timeline)?.body as { clientSessionId: string; startedAt: string }
    expect(create.clientSessionId).toMatch(/^[0-9a-f-]{36}$/)
    expect(new Date(create.startedAt).getTime()).not.toBeNaN()
    const save = api.last('put', sessionsPaths.rawDraft(created.id))?.body as { text: string; baseRevision: number }
    expect(save.text).toContain('Morning coffee')
    expect(save.baseRevision).toBe(created.rawLatestRevision)
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

  test('a save that loses says so, and Keep sends the words again and leaves the cursor in place', async ({ page, api }) => {
    api.mock(sessionsApi.creates(created), sessionsApi.oneIs(created), sessionsApi.draftSaveLoses(created, { draftRevision: 3, latestRevision: 4 }))
    const journal = journalPage(page)
    await journal.open()
    await journal.write('My words')
    await expect(journal.at.conflict('The words on this page are safe in History.')).toBeVisible()
    await expect(journal.at.status('Saved in History at')).toBeVisible()
    await journal.moveTheCursorOneBack()

    api.mock(sessionsApi.draftSaveWins(created, 5))
    await journal.at.keepWords.click()

    await expect(journal.at.status('Saved at')).toBeVisible()
    await expect(journal.at.conflict('The words on this page are safe in History.')).toHaveCount(0)
    await expect(journal.at.composer).toHaveText('My words')
    await expect(journal.at.composer).toBeFocused()
    await journal.typeAfterTheCursor('X')
    await expect(journal.at.composer).toHaveText('My wordXs')
  })

  test('a save that loses can show the newest words, and the next save bases on them', async ({ page, api }) => {
    api.mock(sessionsApi.creates(created), sessionsApi.oneIs(created), sessionsApi.draftSaveLoses(created, { draftRevision: 3, latestRevision: 4 }))
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

  test('a save that wins over a save this page did not show says where the other save is', async ({ page, api }) => {
    api.mock(sessionsApi.creates(created), sessionsApi.draftSaveWins(created, 5))
    const journal = journalPage(page)
    await journal.open()
    await journal.write('My words')

    await expect(journal.at.conflict('The other save is safe in History.')).toBeVisible()
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
    api.mock({ ...sessionsApi.creates(created), delayMs: 300 }, sessionsApi.draftSaveWins(created, 1))
    const journal = journalPage(page)
    await journal.open()
    await journal.typeWithoutWaiting('Words before a move')
    await journal.goToSettings()
    await expect.poll(() => api.all('put', sessionsPaths.rawDraft(created.id)).length).toBe(1)

    const saved = { ...aSession().saying('Words before a move').build(), id: created.id, clientSessionId: created.clientSessionId }
    api.mock(sessionsApi.oneIs(saved), sessionsApi.timelineIs([{ ...aSession().summary('Words before a move'), id: created.id }]))
    await journal.goToTheJournalFromTheSidebar()

    await expect(journal.at.composer).toHaveText('Words before a move')
    await journal.finish()
    await expect(journal.at.entryLink('Words before a move')).toBeVisible()
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

  test('Done during a read of the newest words leaves the next entry empty', async ({ page, api }) => {
    api.mock(sessionsApi.creates(created), sessionsApi.oneIs(created), sessionsApi.draftSaveLoses(created, { draftRevision: 3, latestRevision: 4 }))
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

test.describe('the timeline', () => {
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
    api.mock(sessionsApi.creates(session), sessionsApi.oneIs(session), sessionsApi.draftSaveIsUnchanged(session, { draftRevision: 1, latestRevision: 1 }), sessionsApi.timelineIs([row]))
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
  test.use({ viewport: { width: 390, height: 844 } })

  test('has no sideways scroll with a long word', async ({ page, api }) => {
    const longWord = 'Maximilianbartholomewalexandervanderwesthuizenmontgomerysmythesinclairfitzgeraldworthington'
    api.mock(sessionsApi.timelineIs([aSession().tagged({ topics: [longWord] }).summary(longWord)]))
    const journal = journalPage(page)

    await journal.open()

    await expect(journal.at.entryLink(longWord)).toBeVisible()
    expect(await journal.pageSideScrollRange()).toBe(0)
  })

  test('reaches Logout from the sidebar sheet', async ({ page }) => {
    const journal = journalPage(page)
    await journal.open()

    await journal.at.sidebarToggle.click()

    await expect(journal.at.userMenu).toBeVisible()
  })
})
