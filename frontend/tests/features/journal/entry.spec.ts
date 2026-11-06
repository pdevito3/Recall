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
    await expect(entry.at.editor).toBeFocused()
  })

  test('a save that loses says that it is in History, and Keep sends the words again over the newest save', async ({ page, api }) => {
    const session = aSession().atRevision(4).saying('Mine').build()
    api.mock(sessionsApi.oneIs(session), sessionsApi.draftSaveLoses(session, { draftRevision: 5, latestRevision: 6 }))
    const entry = entryPage(page)
    await entry.open(session.id)
    await entry.edit(session.id, ' edited')
    await expect(entry.at.status('Saved in History at')).toBeVisible()
    const lost = api.last('put', sessionsPaths.rawDraft(session.id))?.body as { savedAt: string }

    api.mock(sessionsApi.draftSaveWins(session, 7))
    await entry.at.keepWords.click()

    await expect(entry.at.status('Saved at')).toBeVisible()
    await expect(entry.at.conflict('The words on this page are safe in History.')).toHaveCount(0)
    const kept = api.last('put', sessionsPaths.rawDraft(session.id))?.body as { baseRevision: number; text: string; savedAt: string }
    expect(kept.baseRevision).toBe(5)
    expect(kept.text).toContain('edited')
    expect(kept.savedAt).not.toBe(lost.savedAt)
    await expect(entry.at.editor).toBeFocused()
  })

  test('words typed during a read of the newest words stay on the page', async ({ page, api }) => {
    const session = aSession().atRevision(4).saying('Mine').build()
    api.mock(sessionsApi.oneIs(session), sessionsApi.draftSaveLoses(session, { draftRevision: 5, latestRevision: 6 }))
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

  test('a save reply during a read of the newest words leaves the words on the page and says what happened', async ({ page, api }) => {
    const session = aSession().atRevision(4).saying('Mine').build()
    api.mock(sessionsApi.oneIs(session), sessionsApi.draftSaveLoses(session, { draftRevision: 5, latestRevision: 6 }))
    const entry = entryPage(page)
    await entry.open(session.id)
    await entry.edit(session.id, ' edited')
    await expect(entry.at.showNewest).toBeVisible()

    api.mock({ ...sessionsApi.oneIs(session), delayMs: 5000 }, sessionsApi.draftSaveWins(session, 7))
    await entry.at.showNewest.click()
    await entry.edit(session.id, ' and more')

    await expect(entry.at.conflict('The other save is safe in History.')).toBeVisible()
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

  test('Keep on a notice leaves the cursor in place', async ({ page, api }) => {
    const session = aSession().atRevision(4).saying('Mine').build()
    api.mock(sessionsApi.oneIs(session), sessionsApi.draftSaveWins(session, 9))
    const entry = entryPage(page)
    await entry.open(session.id)
    await entry.edit(session.id, ' edited')
    await expect(entry.at.keepWords).toBeVisible()
    await entry.moveTheCursorOneBack()

    await entry.at.keepWords.click()
    await expect(entry.at.keepWords).toHaveCount(0)
    await entry.typeAfterTheCursor('X')

    await expect(entry.at.words('Mine editeXd')).toBeVisible()
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

  test('the words of a save that won open after the person comes back, when the read after the lost save failed', async ({ page, api }) => {
    const session = aSession().atRevision(4).saying('Mine').build()
    api.mock(sessionsApi.oneIs(session), sessionsApi.timelineIs([]))
    const entry = entryPage(page)
    await entry.open(session.id)

    api.mock(
      sessionsApi.draftSaveLoses(session, { draftRevision: 5, latestRevision: 6 }),
      sessionsApi.oneFails(session.id, aProblem().refusing('Not now.')),
    )
    await entry.edit(session.id, ' edited')
    await expect(entry.at.status('Saved in History at')).toBeVisible()
    await entry.goToTheJournal()

    const newest = { ...aSession().atRevision(5).saying('From the other device').build(), id: session.id, clientSessionId: session.clientSessionId }
    api.mock(sessionsApi.oneIs(newest))
    await entry.goBack()

    await expect(entry.at.words('From the other device')).toBeVisible()
  })

  test('a save that loses while the person is on another page opens with its words and the notice', async ({ page, api }) => {
    const session = aSession().atRevision(4).saying('Mine').build()
    api.mock(sessionsApi.oneIs(session), sessionsApi.timelineIs([]))
    const entry = entryPage(page)
    await entry.open(session.id)

    api.mock({ ...sessionsApi.draftSaveLoses(session, { draftRevision: 5, latestRevision: 6 }), delayMs: 2000 })
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

  test('a second save that loses on another page shows the notice again on the way back', async ({ page, api }) => {
    const session = aSession().atRevision(4).saying('Mine').build()
    api.mock(sessionsApi.oneIs(session), sessionsApi.timelineIs([]), sessionsApi.draftSaveLoses(session, { draftRevision: 5, latestRevision: 6 }))
    const entry = entryPage(page)
    await entry.open(session.id)
    await entry.edit(session.id, ' edited')
    await expect(entry.at.status('Saved in History at')).toBeVisible()

    api.mock({ ...sessionsApi.draftSaveLoses(session, { draftRevision: 7, latestRevision: 8 }), delayMs: 2000 })
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
    api.mock(sessionsApi.oneIs(session), sessionsApi.draftSaveLoses(session, { draftRevision: 5, latestRevision: 6 }))
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
    await expect(entry.at.conflict('The other save is safe in History.')).toHaveCount(0)
  })

  test('a retry that finds its first try on the Server is a plain save, with no notice', async ({ page, api }) => {
    const session = aSession().atRevision(4).build()
    api.mock(sessionsApi.oneIs(session), sessionsApi.draftSaveIsUnchanged(session, { draftRevision: 5, latestRevision: 5 }))
    const entry = entryPage(page)
    await entry.open(session.id)

    await entry.edit(session.id, ' more')

    await expect(entry.at.status('Saved at')).toBeVisible()
    await expect(entry.at.updatedNotice).toHaveCount(0)
    api.mock(sessionsApi.draftSaveWins(session, 6))
    await entry.edit(session.id, ' again')
    await expect.poll(() => (api.last('put', sessionsPaths.rawDraft(session.id))?.body as { baseRevision: number }).baseRevision).toBe(5)
    await expect(entry.at.conflict('The other save is safe in History.')).toHaveCount(0)
  })

  test('a retry that another save overtook shows the newest words and says so', async ({ page, api }) => {
    const session = aSession().atRevision(5).saying('Mine').build()
    api.mock(sessionsApi.oneIs(session), sessionsApi.draftSaveIsUnchanged(session, { draftRevision: 7, latestRevision: 7 }))
    const entry = entryPage(page)
    await entry.open(session.id)
    const newest = { ...aSession().atRevision(7).saying('From the other device').build(), id: session.id, clientSessionId: session.clientSessionId }
    api.mock(sessionsApi.oneIs(newest))

    await entry.editWithoutWaiting(' edited')

    await expect(entry.at.words('From the other device')).toBeVisible()
    await expect(entry.at.updatedNotice).toBeVisible()
    await expect(entry.at.editor).toBeFocused()
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
