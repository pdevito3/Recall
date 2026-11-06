import { aPerson, peopleApi, peoplePaths } from '@tests/support/api/people-api'
import { sessionsApi, sessionsPaths } from '@tests/support/api/sessions-api'
import { aProblem } from '@tests/support/builders/problem-details-builder'
import { aSession } from '@tests/support/builders/session-builder'
import { aSetting, settingsApi, settingsPaths } from '@tests/support/api/settings-api'
import { expect, test } from '@tests/support/fixtures'
import { journalPage } from './journal.tasks'

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

  test('reads the next page with the cursor when the person scrolls to the end', async ({ page, api }) => {
    const firstPage = Array.from({ length: 40 }, (_, index) => aSession().summary(`Entry number ${index}`))
    api.mock(sessionsApi.timelineIs(firstPage, 'next-page'))
    const journal = journalPage(page)
    await journal.open()
    await expect(journal.at.loadedEntries).toHaveCount(40)

    await journal.scrollToTheEnd()

    await expect.poll(() => api.last('get', sessionsPaths.timeline)?.query.get('cursor')).toBe('next-page')
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
