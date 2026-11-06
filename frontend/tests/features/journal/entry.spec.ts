import { sessionsApi, sessionsPaths } from '@tests/support/api/sessions-api'
import { aProblem } from '@tests/support/builders/problem-details-builder'
import { aSession, documentWith } from '@tests/support/builders/session-builder'
import { expect, test } from '@tests/support/fixtures'
import { entryPage } from './journal.tasks'

test.describe('an entry', () => {
  test('shows its words and a breadcrumb back to the journal', async ({ page, api }) => {
    const session = aSession().saying('Hello there').build()
    api.mock(sessionsApi.oneIs(session))
    const entry = entryPage(page)

    await entry.open(session.id)

    await expect(entry.at.words('Hello there')).toBeVisible()
    await expect(entry.at.breadcrumbName).toBeVisible()
  })

  test('shows words from another client that are not a document, and sends no save', async ({ page, api }) => {
    const session = aSession().withPlainText('Plain words from a phone').build()
    api.mock(sessionsApi.oneIs(session))
    const entry = entryPage(page)

    await entry.open(session.id)

    await expect(entry.at.words('Plain words from a phone')).toBeVisible()
    expect(api.all('put', sessionsPaths.rawDraft(session.id))).toHaveLength(0)
  })

  test('saves an edit based on the Revision that it read', async ({ page, api }) => {
    const session = aSession().atRevision(4).build()
    api.mock(sessionsApi.oneIs(session), sessionsApi.draftSaveWins(session, 5))
    const entry = entryPage(page)
    await entry.open(session.id)

    await entry.edit(session.id, ' more')

    await expect(entry.at.status('Saved at')).toBeVisible()
    const save = api.last('put', sessionsPaths.rawDraft(session.id))?.body as { baseRevision: number; text: string }
    expect(save.baseRevision).toBe(4)
    expect(save.text).toContain('more')
  })

  test('a save that loses keeps the words on the page, says where they are, and can show the newest', async ({ page, api }) => {
    const session = aSession().atRevision(4).saying('Mine').build()
    api.mock(sessionsApi.oneIs(session), sessionsApi.draftSaveLoses(session, { draftRevision: 5, latestRevision: 6 }))
    const entry = entryPage(page)
    await entry.open(session.id)

    await entry.edit(session.id, ' edited')
    await expect(entry.at.conflict('The words on this page are safe in History.')).toBeVisible()
    await expect(entry.at.words('Mine edited')).toBeVisible()

    const newest = aSession().saying('From the other tab').build()
    api.mock(sessionsApi.oneIs({ ...newest, id: session.id, clientSessionId: session.clientSessionId, rawDraftRevision: 5, rawLatestRevision: 6 }))
    await entry.at.showNewest.click()

    await expect(entry.at.words('From the other tab')).toBeVisible()
  })

  test('a save that wins over an unseen save says where the other save is', async ({ page, api }) => {
    const session = aSession().atRevision(4).build()
    api.mock(sessionsApi.oneIs(session), sessionsApi.draftSaveWins(session, 7))
    const entry = entryPage(page)
    await entry.open(session.id)

    await entry.edit(session.id, ' edited')

    await expect(entry.at.conflict('The other save is safe in History.')).toBeVisible()
    await entry.at.keepWords.click()
    await expect(entry.at.conflict('The other save is safe in History.')).toHaveCount(0)
  })

  test('a save that the Server refuses says why, and does not go again', async ({ page, api }) => {
    const session = aSession().build()
    api.mock(sessionsApi.oneIs(session), sessionsApi.draftSaveIsRefused(session, aProblem().refusing('This text is too long.')))
    const entry = entryPage(page)
    await entry.open(session.id)

    await entry.edit(session.id, ' more')

    await expect(entry.at.status('Not saved. This text is too long.')).toBeVisible()
    expect(api.all('put', sessionsPaths.rawDraft(session.id))).toHaveLength(1)
  })

  test('with no reply, the save waits and says so, then it finishes after the connection returns', async ({ page, api }) => {
    const session = aSession().build()
    api.mock(sessionsApi.oneIs(session), sessionsApi.draftSaveHasNoReply(session))
    const entry = entryPage(page)
    await entry.open(session.id)

    await entry.edit(session.id, ' offline words')
    await expect(entry.at.status('The save waits for a connection')).toBeVisible()
    const claimed = (api.last('put', sessionsPaths.rawDraft(session.id))?.body as { savedAt: string }).savedAt

    api.mock(sessionsApi.draftSaveWins(session, 2))
    await entry.signalThatTheConnectionReturned()

    await expect(entry.at.status('Saved at')).toBeVisible()
    const retry = api.last('put', sessionsPaths.rawDraft(session.id))?.body as { text: string; savedAt: string }
    expect(retry.text).toContain('offline words')
    expect(retry.savedAt).toBe(claimed)
  })

  test('a failed load shows the message of the Server in the page', async ({ page, api }) => {
    api.mock(sessionsApi.oneFails('ses_gone', aProblem().notFound('This entry is gone.')))
    const entry = entryPage(page)

    await page.goto('/sessions/ses_gone')

    await expect(entry.at.loadFailure).toContainText('This entry is gone.')
  })
})

test.describe('the tags of an entry', () => {
  test('a mood press sends the whole set with a time', async ({ page, api }) => {
    const session = aSession().tagged({ moods: ['Calm'], topics: ['work'] }).build()
    api.mock(sessionsApi.oneIs(session), sessionsApi.metadataSaves({ ...session, moods: ['Calm', 'Tired'] }))
    const entry = entryPage(page)
    await entry.open(session.id)

    await entry.at.editTags.click()
    await entry.pressMood(session.id, 'Tired')

    const body = api.last('put', sessionsPaths.metadata(session.id))?.body as { moods: string[]; userTopics: string[]; savedAt: string }
    expect(body.moods).toEqual(['Calm', 'Tired'])
    expect(body.userTopics).toEqual(['work'])
    expect(new Date(body.savedAt).getTime()).not.toBeNaN()
    await expect(entry.at.moodChip('Tired')).toHaveAttribute('aria-pressed', 'true')
  })

  test('a topic adds and removes, and an AI topic is not in the set of the person', async ({ page, api }) => {
    const session = aSession().tagged({ topics: ['work'] }).withSuggestedTopic('travel').build()
    api.mock(sessionsApi.oneIs(session), sessionsApi.metadataSaves(session))
    const entry = entryPage(page)
    await entry.open(session.id)

    await entry.at.editTags.click()
    await entry.addTopic(session.id, 'home')

    const body = api.last('put', sessionsPaths.metadata(session.id))?.body as { userTopics: string[] }
    expect(body.userTopics).toEqual(['work', 'home'])
    await expect(entry.at.suggested).toBeVisible()
  })
})

test.describe('the history of an entry', () => {
  test('restores a version as a new save, and keeps the current words as a version', async ({ page, api }) => {
    const session = aSession().atRevision(2).saying('Second words').build()
    api.mock(
      sessionsApi.oneIs(session),
      ...sessionsApi.revisionsAre(session, [{ number: 1, savedAt: '2026-11-06T15:00:00+00:00', text: documentWith('First words') }]),
      sessionsApi.draftSaveWins(session, 3),
    )
    const entry = entryPage(page)
    await entry.open(session.id)

    await entry.openTheFirstVersion()
    await expect(entry.at.versionWords('First words')).toBeVisible()
    await entry.restore(session.id)

    await expect(entry.at.words('First words')).toBeVisible()
    expect((api.last('put', sessionsPaths.rawDraft(session.id))?.body as { text: string }).text).toContain('First words')
  })
})

test.describe('deleting an entry', () => {
  test('asks first, and keeping the entry sends nothing', async ({ page, api }) => {
    const session = aSession().build()
    api.mock(sessionsApi.oneIs(session))
    const entry = entryPage(page)
    await entry.open(session.id)

    await entry.at.deleteButton.click()
    await entry.at.keepEntry.click()

    expect(api.all('delete', sessionsPaths.one(session.id))).toHaveLength(0)
  })

  test('removes the entry after the person confirms, and goes to the journal with one toast', async ({ page, api }) => {
    const session = aSession().build()
    api.mock(sessionsApi.oneIs(session), sessionsApi.deletes(session.id))
    const entry = entryPage(page)
    await entry.open(session.id)

    await entry.confirmDelete()

    expect(api.all('delete', sessionsPaths.one(session.id))).toHaveLength(1)
    expect(await entry.at.deletedToast.count()).toBe(1)
  })
})

test.describe('the cleaned version', () => {
  test('has its own tab when the entry has a cleaned version', async ({ page, api }) => {
    const session = aSession().cleaned('Cleaned words').build()
    api.mock(sessionsApi.oneIs(session))
    const entry = entryPage(page)
    await entry.open(session.id)

    await entry.at.cleanedTab.click()

    await expect(entry.at.cleanedEditor).toContainText('Cleaned words')
  })

  test('has no tab before a cleanup', async ({ page, api }) => {
    const session = aSession().build()
    api.mock(sessionsApi.oneIs(session))
    const entry = entryPage(page)

    await entry.open(session.id)

    await expect(entry.at.cleanedTab).toHaveCount(0)
  })
})
