import { sessionsApi, sessionsPaths } from '@tests/support/api/sessions-api'
import { aProblem } from '@tests/support/builders/problem-details-builder'
import { aSession, documentWith } from '@tests/support/builders/session-builder'
import { expect, test } from '@tests/support/fixtures'
import { STALE_VERSION_PROBLEM_TYPE } from '@/lib/problem-details'
import { entryPage, unsentWords } from './journal.tasks'

const timesIn = (text: string, words: string) => text.split(words).length - 1

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

  test('a save whose words left the Draft keeps the words on the page, says where they are, and can show the newest', async ({ page, api }) => {
    const session = aSession().atRevision(4).saying('Mine').build()
    api.mock(sessionsApi.oneIs(session), sessionsApi.draftSaveIsKept(session, { draftRevision: 5, latestRevision: 6 }))
    const entry = entryPage(page)
    await entry.open(session.id)

    await entry.edit(session.id, ' edited')
    await expect(entry.at.conflict('The words on this page are safe in History.')).toBeVisible()
    await expect(entry.at.words('Mine edited')).toBeVisible()

    const newest = aSession().saying('From the other tab').build()
    api.mock(sessionsApi.oneIs({ ...newest, id: session.id, clientSessionId: session.clientSessionId, rawDraftRevision: 5, rawLatestRevision: 6 }))
    await entry.at.showNewest.click()

    await expect(entry.at.words('From the other tab')).toBeVisible()
    await expect(entry.at.editor).toBeFocused()
  })

  test('a save whose words left the Draft says that they are in History, and Add my words joins only the change of the person to the newest Draft', async ({ page, api }) => {
    const session = aSession().atRevision(4).saying('Mine').build()
    api.mock(sessionsApi.oneIs(session), sessionsApi.draftSaveIsKept(session, { draftRevision: 5, latestRevision: 6 }))
    const entry = entryPage(page)
    await entry.open(session.id)
    await entry.edit(session.id, ' edited')
    await expect(entry.at.status('Saved in History at')).toBeVisible()
    const first = api.last('put', sessionsPaths.rawDraft(session.id))?.body as { savedAt: string }

    const newest = { ...aSession().atRevision(6).saying('Mine', 'From the other device').build(), id: session.id, clientSessionId: session.clientSessionId }
    api.mock(sessionsApi.oneIs(newest), sessionsApi.draftSaveWins(session, 7))
    await entry.at.addMyWords.click()

    await expect(entry.at.status('Saved at')).toBeVisible()
    await expect(entry.at.conflict('The words on this page are safe in History.')).toHaveCount(0)
    const joined = api.last('put', sessionsPaths.rawDraft(session.id))?.body as { baseRevision: number; text: string; savedAt: string }
    expect(joined.baseRevision).toBe(6)
    expect(timesIn(joined.text, 'Mine')).toBe(1)
    expect(timesIn(joined.text, 'Mine edited')).toBe(1)
    expect(timesIn(joined.text, 'From the other device')).toBe(1)
    expect(joined.savedAt).not.toBe(first.savedAt)
    await expect(entry.at.editor).toBeFocused()
  })

  test('words typed during a read of the newest words stay on the page', async ({ page, api }) => {
    const session = aSession().atRevision(4).saying('Mine').build()
    api.mock(sessionsApi.oneIs(session), sessionsApi.draftSaveIsKept(session, { draftRevision: 5, latestRevision: 6 }))
    const entry = entryPage(page)
    await entry.open(session.id)
    await entry.edit(session.id, ' edited')
    await expect(entry.at.showNewest).toBeVisible()

    const newest = { ...aSession().atRevision(5).saying('From the other device').build(), id: session.id, clientSessionId: session.clientSessionId }
    api.mock({ ...sessionsApi.oneIs(newest), delayMs: 500 }, sessionsApi.draftSaveWins(session, 7))
    await entry.at.showNewest.click()
    await entry.editWithoutWaiting(' and more')
    await entry.letAnAnswerHeldForHalfASecondLand()

    await expect(entry.at.words('Mine edited and more')).toBeVisible()
  })

  test('a save to an entry that another tab deleted makes a new entry with the words, says so, and goes there', async ({ page, api }) => {
    const session = aSession().saying('Mine').build()
    const next = aSession().build()
    const saved = { ...aSession().saying('Mine edited').build(), id: next.id, clientSessionId: next.clientSessionId }
    api.mock(
      sessionsApi.oneIs(session),
      sessionsApi.draftSaveIsRefused(session, aProblem().notFound('The entry is gone.')),
      sessionsApi.creates(next),
      sessionsApi.oneIs(saved),
      sessionsApi.draftSaveWins(next, 1),
    )
    const entry = entryPage(page)
    await entry.open(session.id)
    await entry.edit(session.id, ' edited')

    await expect(page).toHaveURL(new RegExp(`/sessions/${next.id}$`))
    await expect(entry.at.movedNotice).toBeVisible()
    await expect.poll(() => JSON.stringify(api.last('put', sessionsPaths.rawDraft(next.id))?.body)).toContain('Mine edited')
    await page.reload()
    await expect(entry.at.words('Mine edited')).toBeVisible()
    await expect.poll(() => page.evaluate(() => Object.keys(localStorage).filter((key) => key.startsWith('recall.unsent')))).toEqual([])
  })

  test('a move to a new entry stores the newest words, and not those of the save that got the 404', async ({ page, api }) => {
    const session = aSession().saying('Mine').build()
    const next = aSession().build()
    api.mock(
      sessionsApi.oneIs(session),
      { ...sessionsApi.draftSaveIsRefused(session, aProblem().notFound('The entry is gone.')), delayMs: 2_000 },
      { ...sessionsApi.creates(next), delayMs: 5_000 },
    )
    const entry = entryPage(page)
    const unsent = unsentWords(page)
    await entry.open(session.id)
    await entry.typeWithoutWaiting(' first')
    await expect.poll(() => api.all('put', sessionsPaths.rawDraft(session.id)).length).toBe(1)

    await entry.typeWithoutWaiting(' newest')
    await expect.poll(() => api.all('post', sessionsPaths.timeline).length).toBe(1)

    const [key] = await unsent.keys()
    expect(key).not.toContain(session.clientSessionId)
    expect(await unsent.stored(key)).toContain('newest')
  })

  test('a first read that started before the save of a reload does not open the old words', async ({ page, api }) => {
    const session = aSession().atRevision(4).saying('Mine').build()
    api.mock(sessionsApi.oneIs(session), sessionsApi.draftSaveHasNoReply(session))
    const entry = entryPage(page)
    await entry.open(session.id)
    await entry.edit(session.id, ' edited')
    await expect(entry.at.status('Not saved yet')).toBeVisible()

    const saved = { ...aSession().atRevision(5).saying('Mine edited').build(), id: session.id, clientSessionId: session.clientSessionId }
    api.mock({ ...sessionsApi.oneIs(session), delayMs: 800 }, sessionsApi.draftSaveWins(session, 5))
    const saveAnswered = entry.aSaveAnswered(session.id)
    await page.reload()
    await saveAnswered
    api.mock(sessionsApi.oneIs(saved))

    await expect(entry.at.words('Mine edited')).toBeVisible()
    await entry.letAnAnswerHeldForHalfASecondLand()
    await expect(entry.at.words('Mine edited')).toBeVisible()
    await expect(entry.at.updatedNotice).toHaveCount(0)
  })

  test('a save reply that lands before the first read of a reload still opens the entry', async ({ page, api }) => {
    const session = aSession().atRevision(4).saying('Mine').build()
    api.mock(sessionsApi.oneIs(session), sessionsApi.draftSaveHasNoReply(session))
    const entry = entryPage(page)
    await entry.open(session.id)
    await entry.edit(session.id, ' edited')
    await expect(entry.at.status('Not saved yet')).toBeVisible()

    const saved = { ...aSession().atRevision(5).saying('Mine edited').build(), id: session.id, clientSessionId: session.clientSessionId }
    api.mock({ ...sessionsApi.oneIs(saved), delayMs: 500 }, sessionsApi.draftSaveWins(session, 5))
    await page.reload()

    await expect(entry.at.words('Mine edited')).toBeVisible()
  })

  test('a lost save whose read fails reads again, and merges and saves once a read works', async ({ page, api }) => {
    const session = aSession().atRevision(4).saying('Mine').build()
    api.mock(sessionsApi.oneIs(session))
    const entry = entryPage(page)
    await entry.open(session.id)

    api.mock(
      sessionsApi.draftSaveLoses(session, { draftRevision: 5, latestRevision: 6 }),
      sessionsApi.oneFails(session.id, aProblem().refusing('Not now.')),
    )
    await entry.edit(session.id, ' edited')
    await expect(entry.at.status('Not saved yet')).toBeVisible()
    expect(api.all('put', sessionsPaths.rawDraft(session.id))).toHaveLength(1)

    const newest = { ...aSession().atRevision(6).saying('Mine', 'From the other device').build(), id: session.id, clientSessionId: session.clientSessionId }
    api.mock(sessionsApi.oneIs(newest), sessionsApi.draftSaveWins(session, 7))

    // The first retry waits 2 s, and the second 4 s.
    await expect(entry.at.mergedNotice).toBeVisible({ timeout: 10_000 })
    await expect(entry.at.status('Saved at')).toBeVisible()
    expect(api.all('put', sessionsPaths.rawDraft(session.id))).toHaveLength(2)
    const merged = api.last('put', sessionsPaths.rawDraft(session.id))?.body as { baseRevision: number; text: string }
    expect(merged.baseRevision).toBe(6)
    expect(timesIn(merged.text, 'Mine edited')).toBe(1)
    expect(timesIn(merged.text, 'From the other device')).toBe(1)
    await expect(entry.at.words('From the other device')).toBeVisible()
    expect(await entry.at.conflict('safe in History').count()).toBe(0)
  })

  test('a kept save whose read fails goes again, and says where the words are once a read works', async ({ page, api }) => {
    const session = aSession().atRevision(4).saying('Mine').build()
    api.mock(sessionsApi.oneIs(session))
    const entry = entryPage(page)
    await entry.open(session.id)

    api.mock(
      sessionsApi.draftSaveIsKept(session, { draftRevision: 5, latestRevision: 6 }),
      sessionsApi.oneFails(session.id, aProblem().refusing('Not now.')),
    )
    await entry.edit(session.id, ' edited')
    await expect(entry.at.status('Not saved yet')).toBeVisible()

    const newest = { ...aSession().atRevision(5).saying('From the other device').build(), id: session.id, clientSessionId: session.clientSessionId }
    api.mock(sessionsApi.oneIs(newest))

    // The first retry waits 2 s, and the second 4 s.
    await expect(entry.at.conflict('The words on this page are safe in History.')).toBeVisible({ timeout: 10_000 })
    await expect(entry.at.words('Mine edited')).toBeVisible()
    await entry.at.showNewest.click()
    await expect(entry.at.words('From the other device')).toBeVisible()
  })

  test('a save whose words left the Draft while the person is on another page opens with its words and the notice', async ({ page, api }) => {
    const session = aSession().atRevision(4).saying('Mine').build()
    api.mock(sessionsApi.oneIs(session), sessionsApi.timelineIs([]))
    const entry = entryPage(page)
    await entry.open(session.id)

    api.mock({ ...sessionsApi.draftSaveIsKept(session, { draftRevision: 5, latestRevision: 6 }), delayMs: 2000 })
    const answered = entry.aSaveAnswered(session.id)
    await entry.editWithoutWaiting(' edited')
    await expect.poll(() => api.all('put', sessionsPaths.rawDraft(session.id)).length).toBe(1)
    const newest = { ...aSession().atRevision(5).saying('From the other device').build(), id: session.id, clientSessionId: session.clientSessionId }
    api.mock(sessionsApi.oneIs(newest))
    await entry.goToTheJournal()
    await answered
    await entry.goBack()

    await expect(entry.at.conflict('The words on this page are safe in History.')).toBeVisible()
    await expect(entry.at.words('Mine edited')).toBeVisible()
    await entry.at.showNewest.click()
    await expect(entry.at.words('From the other device')).toBeVisible()
    await expect(entry.at.conflict('The words on this page are safe in History.')).toHaveCount(0)
  })

  test('a second save whose words left the Draft on another page shows the notice again on the way back', async ({ page, api }) => {
    const session = aSession().atRevision(4).saying('Mine').build()
    api.mock(sessionsApi.oneIs(session), sessionsApi.timelineIs([]), sessionsApi.draftSaveIsKept(session, { draftRevision: 5, latestRevision: 6 }))
    const entry = entryPage(page)
    await entry.open(session.id)
    await entry.edit(session.id, ' edited')
    await expect(entry.at.status('Saved in History at')).toBeVisible()

    api.mock({ ...sessionsApi.draftSaveIsKept(session, { draftRevision: 7, latestRevision: 8 }), delayMs: 2000 })
    const answered = entry.aSaveAnswered(session.id)
    await entry.editWithoutWaiting(' twice')
    await expect.poll(() => api.all('put', sessionsPaths.rawDraft(session.id)).length).toBe(2)
    await entry.goToTheJournal()
    await answered
    await entry.goBack()

    await expect(entry.at.conflict('The words on this page are safe in History.')).toBeVisible()
    await expect(entry.at.words('twice')).toBeVisible()
  })

  test('newest words that do not load say so, and the words on the page stay', async ({ page, api }) => {
    const session = aSession().atRevision(4).saying('Mine').build()
    api.mock(sessionsApi.oneIs(session), sessionsApi.draftSaveIsKept(session, { draftRevision: 5, latestRevision: 6 }))
    const entry = entryPage(page)
    await entry.open(session.id)
    await entry.edit(session.id, ' edited')
    await expect(entry.at.showNewest).toBeVisible()

    api.mock(sessionsApi.oneFails(session.id, aProblem().refusing('Not now.')))
    await entry.at.showNewest.click()

    await expect(entry.at.newestWordsFailure).toBeVisible()
    await expect(entry.at.words('Mine edited')).toBeVisible()
    await expect(entry.at.showNewest).toBeVisible()
  })

  test('a save bases on the Draft, not on a newer Revision that lost', async ({ page, api }) => {
    const session = { ...aSession().atRevision(5).build(), rawLatestRevision: 6 }
    api.mock(sessionsApi.oneIs(session), sessionsApi.draftSaveWins(session, 7))
    const entry = entryPage(page)
    await entry.open(session.id)

    await entry.edit(session.id, ' more')

    await expect(entry.at.status('Saved at')).toBeVisible()
    expect((api.last('put', sessionsPaths.rawDraft(session.id))?.body as { baseRevision: number }).baseRevision).toBe(5)
    await expect(entry.at.conflict('safe in History')).toHaveCount(0)
  })

  test('a retry that finds its first try on the Server is a plain save, with no notice', async ({ page, api }) => {
    const session = aSession().atRevision(4).saying('Mine').build()
    api.mock(sessionsApi.oneIs(session), sessionsApi.draftSaveIsUnchanged(session, { draftRevision: 5, latestRevision: 5 }))
    const entry = entryPage(page)
    await entry.open(session.id)
    api.mock(sessionsApi.oneIs({ ...aSession().atRevision(5).saying('Mine more').build(), id: session.id, clientSessionId: session.clientSessionId }))

    await entry.edit(session.id, ' more')

    await expect(entry.at.status('Saved at')).toBeVisible()
    await expect(entry.at.updatedNotice).toHaveCount(0)
    api.mock(sessionsApi.draftSaveWins(session, 6))
    await entry.edit(session.id, ' again')
    await expect.poll(() => (api.last('put', sessionsPaths.rawDraft(session.id))?.body as { baseRevision: number }).baseRevision).toBe(5)
    await expect(entry.at.conflict('safe in History')).toHaveCount(0)
  })

  test('refused words are on the page again after a reload, with the refusal', async ({ page, api }) => {
    const session = aSession().saying('Mine').build()
    api.mock(sessionsApi.oneIs(session), sessionsApi.draftSaveIsRefused(session, aProblem().refusing('This text is too long.')))
    const entry = entryPage(page)
    await entry.open(session.id)
    await entry.edit(session.id, ' refused')
    await expect(entry.at.status('Not saved. This text is too long.')).toBeVisible()

    await page.reload()

    await expect(entry.at.words('Mine refused')).toBeVisible()
    await expect(entry.at.status('Not saved. This text is too long.')).toBeVisible()
  })

  test('a move between two entries with the history of the browser shows the words of the entry', async ({ page, api }) => {
    const first = aSession().saying('Words of the first entry').build()
    const second = aSession().saying('Words of the second entry').build()
    api.mock(sessionsApi.oneIs(first), sessionsApi.oneIs(second), sessionsApi.timelineIs([{ ...second, ...aSession().summary('The second entry'), id: second.id }]))
    const entry = entryPage(page)
    await entry.open(first.id)
    await entry.goToAnotherEntryThroughTheJournal('The second entry')
    await expect(entry.at.words('Words of the second entry')).toBeVisible()

    await entry.goBackTwoPages()

    await expect(entry.at.words('Words of the first entry')).toBeVisible()
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
    await expect(entry.at.status('Recall keeps trying')).toBeVisible()
    const claimed = (api.last('put', sessionsPaths.rawDraft(session.id))?.body as { savedAt: string }).savedAt

    api.mock(sessionsApi.draftSaveWins(session, 2))
    await entry.signalThatTheConnectionReturned()

    await expect(entry.at.status('Saved at')).toBeVisible()
    const retry = api.last('put', sessionsPaths.rawDraft(session.id))?.body as { text: string; savedAt: string }
    expect(retry.text).toContain('offline words')
    expect(retry.savedAt).toBe(claimed)
  })

  test('a failed load shows the message of the Server in the page', async ({ page, api }) => {
    api.mock(sessionsApi.oneFails('ses_1', aProblem().saying('The database is not there.')))
    const entry = entryPage(page)

    await page.goto('/sessions/ses_1')

    await expect(entry.at.loadFailure).toContainText('The database is not there.')
  })

  test('an entry that does not exist says so, offers the journal, and offers no pointless retry', async ({ page, api }) => {
    api.mock(sessionsApi.oneFails('ses_gone', aProblem().notFound('Session was not found.')))
    const entry = entryPage(page)

    await page.goto('/sessions/ses_gone')

    await expect(entry.at.doesNotExist).toBeVisible()
    await expect(entry.at.tryAgain).toHaveCount(0)
    await entry.leaveForTheJournal()
  })

  test('the question before a delete is an alert dialog', async ({ page, api }) => {
    const session = aSession().build()
    api.mock(sessionsApi.oneIs(session))
    const entry = entryPage(page)
    await entry.open(session.id)

    await entry.at.deleteButton.click()

    await expect(entry.at.deleteDialog).toBeVisible()
  })

  test('words in a script from right to left read from right to left', async ({ page, api }) => {
    const session = aSession().saying('\u05e9\u05dc\u05d5\u05dd').build()
    api.mock(sessionsApi.oneIs(session))
    const entry = entryPage(page)

    await entry.open(session.id)

    await expect(entry.at.editor).toHaveAttribute('dir', 'auto')
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

    const body = api.last('put', sessionsPaths.metadata(session.id))?.body as { moods: string[]; userTopics: string[]; metadataVersion: number; savedAt?: string }
    expect(body.moods).toEqual(['Calm', 'Tired'])
    expect(body.userTopics).toEqual(['work'])
    expect(body.metadataVersion).toBe(session.metadataVersion)
    expect(body.savedAt).toBeUndefined()
    await expect(entry.at.moodChip('Tired')).toHaveAttribute('aria-pressed', 'true')
  })

  test('a change of a tag with no answer yet warns at a close', async ({ page, api }) => {
    const session = aSession().tagged({ moods: ['Calm'], topics: [] }).build()
    api.mock(sessionsApi.oneIs(session), { ...sessionsApi.metadataSaves(session), neverAnswers: true })
    const entry = entryPage(page)
    await entry.open(session.id)
    await entry.at.editTags.click()

    await entry.at.moodChip('Tired').click()

    expect(await unsentWords(page).warnsAtAClose()).toBe(true)
  })

  test('a change of a tag that did not reach the Server asks at a sign-out', async ({ page, api }) => {
    const session = aSession().tagged({ moods: ['Calm'], topics: [] }).build()
    api.mock(sessionsApi.oneIs(session), { method: 'put', path: sessionsPaths.metadata(session.id), status: 503 })
    const entry = entryPage(page)
    const unsent = unsentWords(page)
    await entry.open(session.id)
    await entry.at.editTags.click()
    await entry.at.moodChip('Tired').click()

    await unsent.logOut()

    await expect(unsent.at.tagsLogoutDialog).toBeVisible({ timeout: 10_000 })
    await unsent.answerTheLogoutDialog('Stay signed in')
    await expect(unsent.at.tagsLogoutDialog).toHaveCount(0)
    expect(api.all('post', '/auth/logout')).toHaveLength(0)
  })

  test('a sign-out with unsent words and an unsaved tag names the tag and lets the person stay', async ({ page, api }) => {
    const session = aSession().tagged({ moods: ['Calm'], topics: [] }).build()
    api.mock(sessionsApi.oneIs(session), { method: 'put', path: sessionsPaths.metadata(session.id), status: 503 }, sessionsApi.draftSaveHasNoReply(session))
    const entry = entryPage(page)
    const unsent = unsentWords(page)
    await entry.open(session.id)
    await entry.typeWithoutWaiting('Words that wait')
    await entry.at.editTags.click()
    await entry.at.moodChip('Tired').click()

    await unsent.logOut()

    await expect(unsent.at.logoutDialog).toContainText('A change of your tags also did not reach the server', { timeout: 10_000 })
    await expect(unsent.at.logoutChoice('Discard words and sign out')).toBeInViewport({ ratio: 1 })
    await unsent.answerTheLogoutDialog('Stay signed in')
    await expect(unsent.at.logoutDialog).toHaveCount(0)
    expect(api.all('post', '/auth/logout')).toHaveLength(0)
  })

  test('a question about one tag leaves the status of a failed save in view', async ({ page, api }) => {
    const session = aSession().tagged({ moods: [], topics: [] }).build()
    api.mock(sessionsApi.oneIs(session))
    const entry = entryPage(page)
    await entry.open(session.id)
    await entry.at.editTags.click()

    const elsewhere = { ...session, moods: ['Calm'], metadataVersion: session.metadataVersion + 1 }
    api.mock(sessionsApi.oneIs(elsewhere), { method: 'put', path: sessionsPaths.metadata(session.id), status: 409, body: { type: STALE_VERSION_PROBLEM_TYPE, title: 'Changed', status: 409 } })
    await entry.pressMood(session.id, 'Tired')
    await expect(entry.at.tagQuestion).toBeVisible()

    api.mock({ method: 'put', path: sessionsPaths.metadata(session.id), status: 503 })
    await entry.addTopic(session.id, 'work')

    await expect(entry.at.status('Not saved yet')).toBeVisible()
    await expect(entry.at.tagQuestion).toBeVisible()
  })

  test('a read that started before a mood press does not undo the press', async ({ page, api }) => {
    const session = aSession().tagged({ moods: ['Calm'], topics: [] }).build()
    api.mock(sessionsApi.oneIs(session))
    const entry = entryPage(page)
    await entry.open(session.id)
    await entry.at.editTags.click()

    api.mock({ ...sessionsApi.oneIs(session), delayMs: 500 }, sessionsApi.metadataSaves({ ...session, moods: ['Calm', 'Tired'] }))
    await entry.startARead(session.id)
    await entry.pressMood(session.id, 'Tired')
    await entry.letAnAnswerHeldForHalfASecondLand()

    await expect(entry.at.moodChip('Tired')).toHaveAttribute('aria-pressed', 'true')
  })

  test('a topic is a target of 32 pixels with a mouse, and its removal moves the focus to the next topic and then to the field', async ({ page, api }) => {
    const session = aSession().tagged({ topics: ['work', 'home'] }).build()
    api.mock(sessionsApi.oneIs(session), sessionsApi.metadataSaves({ ...session, topics: session.topics.slice(1) }))
    const entry = entryPage(page)
    await entry.open(session.id)
    await entry.at.editTags.click()

    expect((await entry.at.removeTopic('work').boundingBox())?.width).toBe(32)
    await entry.removeTopic(session.id, 'work')
    await expect(entry.at.removeTopic('home')).toBeFocused()

    api.mock(sessionsApi.metadataSaves({ ...session, topics: [] }))
    await entry.removeTopic(session.id, 'home')
    await expect(entry.at.newTopic).toBeFocused()
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
    await expect(entry.at.topicsFromAi).toBeVisible()
  })

  test('an accepted AI topic is a tag of the entry, so the entry does not say it has no tags', async ({ page, api }) => {
    const session = aSession().withSuggestedTopic('travel').build()
    api.mock(sessionsApi.oneIs(session))
    const entry = entryPage(page)
    await entry.open(session.id)

    await expect(entry.at.topicsFromAi).toBeVisible()
    await expect(entry.at.tag('travel')).toBeVisible()
    await expect(entry.at.noTags).toHaveCount(0)
  })
})

test.describe('the topics of an entry on a phone', () => {
  test.use({ viewport: { width: 390, height: 844 } })

  test('a long topic does not push the page to the side or clip Add', async ({ page, api }) => {
    const topic = 't'.repeat(100)
    const session = aSession().tagged({ topics: [topic, 'short topic'] }).build()
    api.mock(sessionsApi.oneIs(session))
    const entry = entryPage(page)
    await entry.open(session.id)

    await entry.at.editTags.click()

    await entry.at.addTopic.scrollIntoViewIfNeeded()
    await expect(entry.at.addTopic).toBeInViewport({ ratio: 1 })
    await expect(entry.at.removeTopic(topic)).toBeInViewport({ ratio: 1 })
    expect(await entry.sideScrollRange()).toBe(0)
    expect((await entry.at.removeTopic(topic).boundingBox())?.width).toBe(44)
  })
})

test.describe('an entry that another device changed', () => {
  test('on a phone, a save that loses merges the words of the person into the newest words, saves them, and says so', async ({ page, api }) => {
    await page.setViewportSize({ width: 390, height: 844 })
    const session = aSession().atRevision(4).saying('Mine').build()
    api.mock(sessionsApi.oneIs(session))
    const entry = entryPage(page)
    await entry.open(session.id)
    const readsBefore = api.all('get', sessionsPaths.one(session.id)).length

    api.mock({ ...sessionsApi.draftSaveLoses(session, { draftRevision: 5, latestRevision: 6 }), delayMs: 300 })
    await entry.typeWithoutWaiting(' edited')
    await expect.poll(() => api.all('put', sessionsPaths.rawDraft(session.id)).length).toBe(1)
    const newest = { ...aSession().atRevision(6).saying('Mine', 'From the other device').build(), id: session.id, clientSessionId: session.clientSessionId }
    api.mock(sessionsApi.oneIs(newest), sessionsApi.draftSaveWins(session, 7))

    await expect(entry.at.mergedNotice).toBeVisible()
    await expect(entry.at.status('Saved at')).toBeVisible()
    expect(api.all('get', sessionsPaths.one(session.id)).length).toBeGreaterThan(readsBefore)
    const [lost, merged, ...more] = api.all('put', sessionsPaths.rawDraft(session.id)).map(({ body }) => body as { baseRevision: number; text: string; savedAt: string })
    expect(more).toEqual([])
    expect(merged.baseRevision).toBe(6)
    expect(merged.savedAt).not.toBe(lost.savedAt)
    expect(timesIn(merged.text, 'Mine')).toBe(1)
    expect(timesIn(merged.text, 'Mine edited')).toBe(1)
    expect(timesIn(merged.text, 'From the other device')).toBe(1)
    await expect(entry.at.words('Mine edited')).toBeVisible()
    await expect(entry.at.words('From the other device')).toBeVisible()
    expect(await entry.at.conflict('safe in History').count()).toBe(0)
    await expect(entry.at.mergedNotice).toBeInViewport({ ratio: 1 })
    await expect(entry.at.dismissMerged).toBeInViewport({ ratio: 1 })
    expect(await entry.sideScrollRange()).toBe(0)

    await entry.at.dismissMerged.click()

    await expect(entry.at.mergedNotice).toHaveCount(0)
  })

  test('after 3 saves that lose in a row, the next read waits, the status says so, and the words stay', async ({ page, api }) => {
    const session = aSession().atRevision(4).saying('Mine').build()
    api.mock(sessionsApi.oneIs(session))
    const entry = entryPage(page)
    await entry.open(session.id)
    const newest = { ...aSession().atRevision(6).saying('Mine', 'From the other device').build(), id: session.id, clientSessionId: session.clientSessionId }
    api.mock(sessionsApi.draftSaveLoses(session, { draftRevision: 5, latestRevision: 6 }), sessionsApi.oneIs(newest))
    const saves = entry.timesOfDraftSaves(session.id)

    await entry.typeWithoutWaiting(' edited')

    await expect(entry.at.status('Another tab or device keeps changing this entry. Your words are safe, and they wait to save.')).toBeVisible({ timeout: 10_000 })
    expect(saves).toHaveLength(3)
    await expect(entry.at.words('Mine edited')).toBeVisible()
    await expect.poll(() => saves.length, { timeout: 10_000 }).toBe(4)
    expect(saves[3].sentAt - (saves[2].answeredAt ?? Infinity)).toBeGreaterThanOrEqual(900)
    await expect(entry.at.words('Mine edited')).toBeVisible()
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

  test('a closed history shows no button for older versions', async ({ page, api }) => {
    const session = aSession().atRevision(2).saying('Second words').build()
    api.mock(
      sessionsApi.oneIs(session),
      ...sessionsApi.revisionsAre(session, [{ number: 1, savedAt: '2026-11-06T15:00:00+00:00', text: documentWith('First words') }], 'more'),
    )
    const entry = entryPage(page)
    await entry.open(session.id)

    await entry.at.showHistory.click()
    await expect(entry.at.olderVersions).toBeVisible()
    await entry.at.closeHistory.click()

    await expect(entry.at.olderVersions).toHaveCount(0)
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

  for (const { order, saveMs, deleteMs } of [
    { order: 'while the delete runs', saveMs: 4_000, deleteMs: 6_000 },
    { order: 'after the delete', saveMs: 4_000, deleteMs: 0 },
  ]) {
    test(`a save that answers 404 ${order} does not bring the words back`, async ({ page, api }) => {
      const session = aSession().saying('Mine').build()
      api.mock(
        sessionsApi.oneIs(session),
        { ...sessionsApi.draftSaveIsRefused(session, aProblem().notFound('The entry is gone.')), delayMs: saveMs },
        { ...sessionsApi.deletes(session.id), delayMs: deleteMs },
        sessionsApi.creates(aSession().build()),
      )
      const entry = entryPage(page)
      const unsent = unsentWords(page)
      await entry.open(session.id)
      const answered = entry.aDraftSaveAnswered(session.id)
      await entry.typeWithoutWaiting(' and words to delete')
      await expect.poll(() => api.all('put', sessionsPaths.rawDraft(session.id)).length).toBe(1)

      await entry.confirmDelete()
      await answered
      await expect(entry.at.deletedToast).toBeVisible({ timeout: 10_000 })
      await unsent.letAnAnswerLand()

      expect(api.all('post', sessionsPaths.timeline)).toHaveLength(0)
      expect(await unsent.keys()).toEqual([])
      await expect(unsent.at.wholePage).not.toContainText('Your words are safe')
    })
  }
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
