import type { BrowserContext, Page } from '@playwright/test'
import type { SessionDto } from '@/api/generated/models/types/session-dto'
import { authApi, authPaths } from '@tests/support/api/auth-api'
import { aSetting, settingsApi, settingsPaths } from '@tests/support/api/settings-api'
import { sessionsApi, sessionsPaths } from '@tests/support/api/sessions-api'
import type { MockRoute } from '@tests/support/api/mock-route'
import { actorSubject } from '@tests/support/auth/actors'
import { aProblem } from '@tests/support/builders/problem-details-builder'
import { aSession } from '@tests/support/builders/session-builder'
import { expect, test, type Api } from '@tests/support/fixtures'
import { entryPage, journalPage, unsentWords } from './journal.tasks'

const wordsOf = (request: { body: unknown } | undefined) => JSON.stringify(request?.body)

const signInPage: MockRoute = { method: 'get', path: '/auth/login', status: 200, body: {} }
const logoutWorks: MockRoute = { method: 'post', path: '/auth/logout', status: 204 }

const anotherPerson = {
  build: () => ({
    type: 'urn:recall:problem:page-user-mismatch',
    title: 'This page belongs to another person',
    status: 409,
    detail: 'Another person signed in on this browser.',
  }),
}

test.describe('a page of one person on a browser where another person signed in', () => {
  test('each save names the user of the page', async ({ page, api }) => {
    const session = aSession().build()
    api.mock(sessionsApi.creates(session), sessionsApi.draftSaveWins(session, 1))
    const journal = journalPage(page)
    await journal.open()

    await journal.write('Words of one person')

    expect(api.last('post', sessionsPaths.timeline)?.headers.get('x-recall-user')).toBe(actorSubject)
    expect(api.last('put', sessionsPaths.rawDraft(session.id))?.headers.get('x-recall-user')).toBe(actorSubject)
  })

  test('a refused save stops the saves, clears the page, keeps the words for the first person, and goes to the sign-in page', async ({ page, api }) => {
    const unsent = unsentWords(page)
    const session = aSession().build()
    api.mock(signInPage, sessionsApi.creates(session), sessionsApi.draftSaveWins(session, 1))
    const journal = journalPage(page)
    await journal.open()
    await journal.write('Private words')
    await expect(journal.at.status('Saved at')).toBeVisible()

    api.mock(sessionsApi.draftSaveIsRefused(session, anotherPerson))
    await journal.typeWithoutWaiting(' and more')

    await unsent.reachTheSignInPageForAnotherPerson()
    await expect(unsent.at.wholePage).not.toContainText('Private words')
    expect(await unsent.composerPointer()).toBeNull()
    const [key] = await unsent.keys()
    expect(key).toContain(`recall.unsent.${actorSubject}.`)
    expect(await unsent.stored(key)).toContain('and more')
    expect(api.all('put', sessionsPaths.rawDraft(session.id))).toHaveLength(2)
  })

  test('a refused read also leaves the page', async ({ page, api }) => {
    const unsent = unsentWords(page)
    const session = aSession().saying('Private words').build()
    api.mock(signInPage, sessionsApi.oneIs(session))
    const entry = entryPage(page)
    await entry.open(session.id)

    api.mock(sessionsApi.oneFails(session.id, anotherPerson))
    await unsent.comeBackToTheTab()

    await unsent.reachTheSignInPageForAnotherPerson()
    await expect(unsent.at.wholePage).not.toContainText('Private words')
  })
})

test.describe('words for an entry that was deleted', () => {
  test('from the outbox on a later load with no page open, they make a new entry and the person gets a notice', async ({ page, api }) => {
    const unsent = unsentWords(page)
    const gone = aSession().build()
    const next = aSession().build()
    api.mock(
      sessionsApi.rawRevisionFails(gone, 1, aProblem().notFound('This Session does not exist.')),
      sessionsApi.draftSaveIsRefused(gone, aProblem().notFound('This Session does not exist.')),
      sessionsApi.creates(next),
      sessionsApi.oneIs(next),
      sessionsApi.draftSaveWins(next, 1),
    )
    await unsent.leftInTheOutbox({ words: 'Words for a deleted entry', sessionId: gone.id })

    await unsent.openSettings()

    await expect(unsent.at.movedNotice).toContainText('Your words are safe in a new entry')
    await expect.poll(() => wordsOf(api.last('put', sessionsPaths.rawDraft(next.id)))).toContain('Words for a deleted entry')
    await expect.poll(() => unsent.keys()).toEqual([])
    await unsent.openTheNewEntry()
    await expect(page).toHaveURL(new RegExp(`/sessions/${next.id}$`))
    await unsent.dismissTheMovedNotice()
    await expect(unsent.at.movedNotice).toHaveCount(0)
  })

  test('while the composer is open, they go on into a new entry and the person gets a notice', async ({ page, api }) => {
    const unsent = unsentWords(page)
    const gone = aSession().build()
    const next = aSession().build()
    api.mock(sessionsApi.creates(gone), sessionsApi.draftSaveWins(gone, 1))
    const journal = journalPage(page)
    await journal.open()
    await journal.write('First words')
    await expect(journal.at.status('Saved at')).toBeVisible()

    api.mock(sessionsApi.draftSaveIsRefused(gone, aProblem().notFound('This Session does not exist.')), sessionsApi.creates(next), sessionsApi.draftSaveWins(next, 1))
    await journal.typeWithoutWaiting(' and more')

    await expect(unsent.at.movedNotice).toBeVisible()
    await expect(journal.at.composer).toHaveText('First words and more')
    await expect.poll(() => wordsOf(api.last('put', sessionsPaths.rawDraft(next.id)))).toContain('First words and more')
    await journal.typeWithoutWaiting(' again')
    await expect.poll(() => wordsOf(api.last('put', sessionsPaths.rawDraft(next.id)))).toContain('First words and more again')
    expect(api.all('post', sessionsPaths.timeline)).toHaveLength(2)
  })
})

test.describe('words that the Server refused', () => {
  test('do not go out again on the next load, and the composer shows them with the reason', async ({ page, api }) => {
    const session = aSession().build()
    const tooLong = aProblem().refusing('This text is too long.')
    api.mock(sessionsApi.creates(session), sessionsApi.oneIs(session), sessionsApi.draftSaveIsRefused(session, tooLong))
    const journal = journalPage(page)
    await journal.open()
    await journal.write('Too many words')
    await expect(journal.at.status('Not saved.')).toBeVisible()

    await journal.reload()

    await expect(journal.at.composer).toHaveText('Too many words')
    await expect(journal.at.status('Not saved. This text is too long.')).toBeVisible()
    await journal.letAnAnswerHeldForHalfASecondLand()
    expect(api.all('put', sessionsPaths.rawDraft(session.id))).toHaveLength(1)
  })

  test('on an entry longer than the screen, the reason stays in view', async ({ page, api }) => {
    const session = aSession().saying(...Array.from({ length: 120 }, (_, line) => `Line ${line}`)).build()
    api.mock(sessionsApi.oneIs(session), sessionsApi.draftSaveIsRefused(session, aProblem().refusing('This text is too long.')))
    const entry = entryPage(page)
    await entry.open(session.id)

    await entry.edit(session.id, ' more')

    await expect(entry.at.status('Not saved. This text is too long.')).toBeInViewport()
  })

  test('in a composer longer than the screen, the reason and Done stay in view', async ({ page, api }) => {
    const session = aSession().build()
    api.mock(sessionsApi.creates(session), sessionsApi.draftSaveIsRefused(session, aProblem().refusing('This text is too long.')))
    const journal = journalPage(page)
    await journal.open()

    await journal.typeWithoutWaiting('A line\n'.repeat(60))

    await expect(journal.at.status('Not saved. This text is too long.')).toBeInViewport()
    await expect(journal.at.done).toBeInViewport()
  })

  test('with no page open, a notice gives the reason, the words, and a copy, and the discard removes them', async ({ page, context }) => {
    const unsent = unsentWords(page)
    await context.grantPermissions(['clipboard-read', 'clipboard-write'])
    const key = await unsent.leftInTheOutbox({ words: 'Too many words', sessionId: null, refusal: 'This text is too long.' })

    await unsent.openSettings()

    const notice = unsent.at.refusedNotice
    await expect(notice).toContainText('This text is too long.')
    await expect(notice).toContainText('Too many words')
    await unsent.copyTheRefusedWords()
    await expect.poll(() => unsent.clipboardText()).toBe('Too many words')
    await page.reload()
    await expect(notice).toBeVisible()
    await unsent.discardTheRefusedWords()
    await expect(notice).toHaveCount(0)
    expect(await unsent.stored(key)).toBeNull()
  })
})

test.describe('the outbox', () => {
  test('sends the words of an earlier load with no page open', async ({ page, api }) => {
    const unsent = unsentWords(page)
    const session = aSession().build()
    api.mock(sessionsApi.rawRevisionIs(session, 1), sessionsApi.draftSaveWins(session, 2))
    const key = await unsent.leftInTheOutbox({ words: 'Words of an earlier load', sessionId: session.id })

    await unsent.openSettings()

    await expect.poll(() => wordsOf(api.last('put', sessionsPaths.rawDraft(session.id)))).toContain('Words of an earlier load')
    await expect.poll(() => unsent.stored(key)).toBeNull()
  })

  test('keeps the words of an earlier version in their key while the read of their base fails, and sends them when it works', async ({ page, api }) => {
    const unsent = unsentWords(page)
    const session = aSession().build()
    api.mock(sessionsApi.rawRevisionFails(session, 1, aProblem()), sessionsApi.draftSaveWins(session, 2))
    await unsent.leftInTheOutbox({ words: 'Words of an earlier version', sessionId: session.id })

    await unsent.openSettings()

    await expect.poll(() => api.all('get', sessionsPaths.rawRevision(session.id, 1)).length).toBeGreaterThan(0)
    await expect(unsent.at.waiting).toContainText('Words of an earlier version')
    expect(api.all('put', sessionsPaths.rawDraft(session.id))).toHaveLength(0)
    expect(await unsent.wordsInTheKeys()).toEqual(['Words of an earlier version'])

    api.mock(sessionsApi.rawRevisionIs(session, 1))
    await expect.poll(() => wordsOf(api.last('put', sessionsPaths.rawDraft(session.id))), { timeout: 15_000 }).toContain('Words of an earlier version')
    await expect.poll(() => unsent.keys()).toEqual([])
  })

  test('does not send, show, or remove the words of another person', async ({ page, api }) => {
    const unsent = unsentWords(page)
    const session = aSession().build()
    const key = await unsent.leftInTheOutbox({ words: 'Words of another person', sessionId: session.id, owner: 'usr_another' })

    await unsent.openSettings()
    await unsent.letAnAnswerLand()

    expect(api.all('put', sessionsPaths.rawDraft(session.id))).toHaveLength(0)
    await expect(unsent.at.wholePage).not.toContainText('Words of another person')
    expect(await unsent.stored(key)).toContain('Words of another person')
  })

  test('sends each entry that it can read, and removes an entry that it cannot read', async ({ page, api }) => {
    const unsent = unsentWords(page)
    const session = aSession().build()
    api.mock(sessionsApi.rawRevisionIs(session, 1), sessionsApi.draftSaveWins(session, 2))
    const literalNull = `recall.unsent.${actorSubject}.literal-null.raw`
    const broken = `recall.unsent.${actorSubject}.broken.raw`
    await unsent.leftInStorage(literalNull, 'null')
    await unsent.leftInStorage(broken, '{broken')
    await unsent.leftInTheOutbox({ words: 'Words that can be read', sessionId: session.id })

    await unsent.openSettings()

    await expect.poll(() => wordsOf(api.last('put', sessionsPaths.rawDraft(session.id)))).toContain('Words that can be read')
    expect(await unsent.stored(literalNull)).toBeNull()
    expect(await unsent.stored(broken)).toBeNull()
  })

  test('a delete of an entry removes its unsent words', async ({ page, api }) => {
    const unsent = unsentWords(page)
    const session = aSession().saying('Mine').build()
    api.mock(sessionsApi.oneIs(session), sessionsApi.draftSaveHasNoReply(session), sessionsApi.deletes(session.id))
    const entry = entryPage(page)
    await entry.open(session.id)
    await entry.edit(session.id, ' unsent')
    expect(await unsent.keys()).toHaveLength(1)

    await entry.at.deleteButton.click()
    await entry.at.confirmDelete.click()

    await expect(entry.at.deletedToast).toBeVisible()
    expect(await unsent.keys()).toEqual([])
  })
})

test.describe('Done', () => {
  test('sends the words at once and does not wait for the pause in typing', async ({ page, api }) => {
    const session = aSession().build()
    api.mock(sessionsApi.creates(session), sessionsApi.draftSaveWins(session, 1))
    const journal = journalPage(page)
    await page.clock.install()
    await journal.open()
    await page.clock.pauseAt(Date.now() + 60_000)

    await journal.typeWithoutWaiting('Finished words')
    await journal.finish()

    await expect.poll(() => wordsOf(api.last('put', sessionsPaths.rawDraft(session.id)))).toContain('Finished words')
  })
})

test.describe('the status line', () => {
  test('says Saved only after the Server holds the words', async ({ page, api }) => {
    const session = aSession().build()
    api.mock(sessionsApi.creates(session), { ...sessionsApi.draftSaveWins(session, 1), delayMs: 400 })
    const journal = journalPage(page)
    await journal.open()

    await journal.typeWithoutWaiting('Slow words')

    await expect(journal.at.status('Saving')).toBeVisible()
    await expect(journal.at.status('Saved at')).toHaveCount(0)
    await expect(journal.at.status('Saved at')).toBeVisible()
  })

  test('does not say Saved for words that a newer save pushed out of the Draft', async ({ page, api }) => {
    const session = aSession().build()
    api.mock(sessionsApi.creates(session), sessionsApi.oneIs(session), sessionsApi.draftSaveIsKept(session, { draftRevision: 2, latestRevision: 3 }))
    const journal = journalPage(page)
    await journal.open()

    await journal.write('Words that left the Draft')

    await expect(journal.at.status('Saved in History at')).toBeVisible()
    await expect(journal.at.savedAtStart).toHaveCount(0)
  })

  test('a save that hangs ends at the request limit, and the status tells the truth', async ({ page, api }) => {
    const session = aSession().build()
    api.mock(sessionsApi.creates(session), { ...sessionsApi.draftSaveWins(session, 1), delayMs: 2_000 })
    const journal = journalPage(page)
    await page.clock.install()
    await journal.open()
    await journal.typeWithoutWaiting('Words on a slow line')
    await page.clock.fastForward(1_000)
    await expect(journal.at.status('Saving')).toBeVisible()

    await page.clock.fastForward(31_000)

    await expect(journal.at.status('Recall keeps trying')).toBeVisible()
  })
})

test.describe('a return to an entry whose read is slow', () => {
  test('keeps the keys typed during the read, and makes no second entry', async ({ page, api }) => {
    const session = aSession().build()
    api.mock(sessionsApi.creates(session), sessionsApi.oneIs(session), sessionsApi.draftSaveWins(session, 1))
    const journal = journalPage(page)
    await journal.open()
    await journal.write('Words from before')
    await expect(journal.at.status('Saved at')).toBeVisible()
    await journal.goToSettings()

    const saved = { ...aSession().atRevision(1).saying('Words from before').build(), id: session.id, clientSessionId: session.clientSessionId }
    api.mock({ ...sessionsApi.oneIs(saved), delayMs: 6_000 })
    await page.reload()
    await expect(journal.at.loadingEntry).toHaveCount(0)
    await journal.goToTheJournalFromTheSidebar()
    await expect(journal.at.loadingEntry).toBeVisible()
    await journal.typeWithoutWaiting('Words typed early')
    await expect(journal.at.loadingEntry).toBeVisible({ timeout: 1_000 })

    await expect(journal.at.composer).toContainText('Words from before', { timeout: 10_000 })
    await expect(journal.at.composer).toContainText('Words typed early')
    await expect.poll(() => wordsOf(api.last('put', sessionsPaths.rawDraft(session.id)))).toContain('Words typed early')
    expect(wordsOf(api.last('put', sessionsPaths.rawDraft(session.id)))).toContain('Words from before')
    expect(api.all('post', sessionsPaths.timeline)).toHaveLength(1)
  })

  const aReturnDuringASlowRead = async (journal: ReturnType<typeof journalPage>, page: Page, api: Api, session: SessionDto, slowRead: (saved: SessionDto) => MockRoute) => {
    api.mock(sessionsApi.creates(session), sessionsApi.oneIs(session), sessionsApi.draftSaveWins(session, 1))
    await journal.open()
    await journal.write('Words from before')
    await expect(journal.at.status('Saved at')).toBeVisible()
    await journal.goToSettings()

    const saved = { ...aSession().atRevision(1).saying('Words from before').build(), id: session.id, clientSessionId: session.clientSessionId }
    api.mock(slowRead(saved))
    await page.reload()
    await expect(journal.at.loadingEntry).toHaveCount(0)
    await journal.goToTheJournalFromTheSidebar()
    await expect(journal.at.loadingEntry).toBeVisible()
  }

  // A move away cancels a read that takes long, and a cancelled call cannot have a mocked answer. So this read fails at once and tries again.
  const aReturnDuringAFailingRead = async (journal: ReturnType<typeof journalPage>, page: Page, api: Api, session: SessionDto) => {
    let savedEntry: SessionDto | undefined
    await aReturnDuringASlowRead(journal, page, api, session, (saved) => {
      savedEntry = saved
      return sessionsApi.oneFails(saved.id, aProblem().saying('The Server is busy.'))
    })
    return savedEntry!
  }

  test('a person who leaves during the read does not lose the keys, and the keys do not replace the entry', async ({ page, api }) => {
    const unsent = unsentWords(page)
    const session = aSession().build()
    const journal = journalPage(page)
    const saved = await aReturnDuringAFailingRead(journal, page, api, session)
    await journal.typeWithoutWaiting('Words typed early')
    api.mock({ ...sessionsApi.draftSaveWins(session, 2), delayMs: 3_000 })

    await journal.goToSettings()
    await unsent.letAnAnswerLand()
    expect(api.all('put', sessionsPaths.rawDraft(session.id))).toHaveLength(1)
    api.mock(sessionsApi.oneIs(saved))
    await expect.poll(() => wordsOf(api.last('put', sessionsPaths.rawDraft(session.id))), { timeout: 10_000 }).toContain('Words typed early')
    expect(wordsOf(api.last('put', sessionsPaths.rawDraft(session.id)))).toContain('Words from before')

    await journal.goToTheJournalFromTheSidebar()
    await expect(journal.at.composer).toContainText('Words from before')
    await expect(journal.at.composer).toContainText('Words typed early')
  })

  test('a person who comes back before the read answered finds the keys, and can add more', async ({ page, api }) => {
    const unsent = unsentWords(page)
    const session = aSession().build()
    const journal = journalPage(page)
    const saved = await aReturnDuringAFailingRead(journal, page, api, session)
    await journal.typeWithoutWaiting('Words typed early')

    await journal.goToSettings()
    await unsent.letAnAnswerLand()
    await journal.goToTheJournalFromTheSidebar()
    await expect(journal.at.loadingEntry).toBeVisible()
    await journal.typeWithoutWaiting('Words typed later')
    api.mock(sessionsApi.oneIs(saved), sessionsApi.draftSaveWins(session, 2))

    await expect(journal.at.composer).toContainText('Words typed early', { timeout: 10_000 })
    await expect(journal.at.composer).toContainText('Words from before')
    await expect(journal.at.composer).toContainText('Words typed later')
    await expect.poll(() => wordsOf(api.last('put', sessionsPaths.rawDraft(session.id)))).toContain('Words typed later')
    expect(wordsOf(api.last('put', sessionsPaths.rawDraft(session.id)))).toContain('Words from before')
    expect(wordsOf(api.last('put', sessionsPaths.rawDraft(session.id)))).toContain('Words typed early')
  })

  test('a key on the entry page during a hold keeps the held words, and the page shows them', async ({ page, api }) => {
    const session = aSession().build()
    const journal = journalPage(page)
    const entry = entryPage(page)
    const unsent = unsentWords(page)
    const saved = { ...aSession().atRevision(1).saying('Words from before').build(), id: session.id }
    await aReturnDuringASlowRead(journal, page, api, session, () => ({ ...sessionsApi.oneIs(saved), neverAnswers: true }))
    await journal.typeWithoutWaiting('Words typed early')

    // The read of a mock does not change with a save, so the save must answer after the page opens.
    api.mock(
      sessionsApi.oneIs({ ...saved, clientSessionId: await unsent.clientSessionIdOfTheHold() }),
      { ...sessionsApi.draftSaveWins(session, 2), delayMs: 3_000 },
    )
    await entry.open(session.id)
    await entry.typeWithoutWaiting(' a key on the entry page')

    await expect(entry.at.editor).toContainText('Words typed early', { timeout: 10_000 })
    await expect(entry.at.editor).toContainText('a key on the entry page')
    await expect.poll(() => wordsOf(api.last('put', sessionsPaths.rawDraft(session.id))), { timeout: 10_000 }).toContain('Words typed early')
    await expect.poll(() => wordsOf(api.last('put', sessionsPaths.rawDraft(session.id))), { timeout: 10_000 }).toContain('a key on the entry page')
  })

  test('Edit here in a second tab during the read opens the held words there, and saves them on the newest words', async ({ page, context, api }) => {
    const session = aSession().build()
    const journal = journalPage(page)
    const saved = { ...aSession().atRevision(1).saying('Words from before').build(), id: session.id }
    await aReturnDuringASlowRead(journal, page, api, session, () => ({ ...sessionsApi.oneIs(saved), neverAnswers: true }))
    await journal.typeWithoutWaiting('Words typed early')
    await expect.poll(() => unsentWords(page).clientSessionIdOfTheHold()).toBeTruthy()
    // A phone saved Revision 2 since, and the second tab reads it.
    const onThePhone = { ...aSession().atRevision(2).saying('Words from before and the phone').build(), id: session.id, clientSessionId: await unsentWords(page).clientSessionIdOfTheHold() }
    api.mock(sessionsApi.oneIs(onThePhone), sessionsApi.draftSaveWins(session, 3))
    const second = entryPage(await context.newPage())
    await second.open(session.id)

    await second.at.editHere.click()

    await expect(second.at.editor).toContainText('Words from before and the phone')
    await expect(second.at.editor).toContainText('Words typed early')
    await expect.poll(() => wordsOf(api.last('put', sessionsPaths.rawDraft(session.id))), { timeout: 10_000 }).toContain('Words typed early')
    expect(wordsOf(api.last('put', sessionsPaths.rawDraft(session.id)))).toContain('Words from before and the phone')
    expect(api.last('put', sessionsPaths.rawDraft(session.id))?.body).toMatchObject({ baseRevision: 2 })
  })

  test('an entry page that opens before its lock answers opens the held words of a reload at the grant', async ({ page, api }) => {
    const session = aSession().build()
    const journal = journalPage(page)
    const entry = entryPage(page)
    const saved = { ...aSession().atRevision(1).saying('Words from before').build(), id: session.id }
    await aReturnDuringASlowRead(journal, page, api, session, () => ({ ...sessionsApi.oneIs(saved), neverAnswers: true }))
    await journal.typeWithoutWaiting('Words typed early')
    await expect.poll(() => unsentWords(page).clientSessionIdOfTheHold()).toBeTruthy()
    api.mock(sessionsApi.oneIs({ ...saved, clientSessionId: await unsentWords(page).clientSessionIdOfTheHold() }), sessionsApi.draftSaveWins(session, 2))
    await unsentWords(page).locksAnswerLate()

    await entry.open(session.id)

    await expect(entry.at.editor).toContainText('Words from before', { timeout: 10_000 })
    await expect(entry.at.editor).toContainText('Words typed early')
    await expect.poll(() => wordsOf(api.last('put', sessionsPaths.rawDraft(session.id))), { timeout: 10_000 }).toContain('Words typed early')
  })

  test('the editor stays the same at the answer of a read, so each key lands in the words', async ({ page, api }) => {
    const session = aSession().build()
    const journal = journalPage(page)
    const saved = { ...aSession().atRevision(1).saying('Words from before').build(), id: session.id, clientSessionId: session.clientSessionId }
    await aReturnDuringASlowRead(journal, page, api, session, () => ({ ...sessionsApi.oneIs(saved), delayMs: 500 }))
    await journal.at.composer.click()
    await journal.markTheEditor()

    await page.keyboard.type('abcdefghijklmnopqrstuvwxyz', { delay: 40 })

    await expect(journal.at.loadingEntry).toHaveCount(0)
    expect(await journal.isTheSameEditor()).toBe(true)
    await expect(journal.at.composer).toContainText('Words from before')
    await expect(journal.at.composer).toContainText('abcdefghijklmnopqrstuvwxyz')
  })

  test('a composer that opens with no network reads the entry when the network returns, and joins the keys', async ({ page, api }) => {
    const session = aSession().build()
    const journal = journalPage(page)
    api.mock(sessionsApi.creates(session), sessionsApi.oneIs(session), sessionsApi.draftSaveWins(session, 1))
    await journal.open()
    await journal.write('Words from before')
    await expect(journal.at.status('Saved at')).toBeVisible()
    await journal.goToSettings()
    const saved = { ...aSession().atRevision(1).saying('Words from before').build(), id: session.id, clientSessionId: session.clientSessionId }
    api.mock(sessionsApi.oneIs(saved), sessionsApi.draftSaveWins(session, 2))
    await page.reload()
    await journal.settingsIsOpen()
    await page.context().setOffline(true)
    await journal.goToTheJournalFromTheSidebar()
    await expect(journal.at.loadingEntry).toBeVisible()
    await journal.typeWithoutWaiting('Words typed offline')

    await page.context().setOffline(false)

    await expect(journal.at.loadingEntry).toHaveCount(0)
    await expect(journal.at.composer).toContainText('Words from before')
    await expect(journal.at.composer).toContainText('Words typed offline')
  })

  test('a composition that runs when the read answers keeps each word', async ({ page, api }) => {
    const session = aSession().build()
    const journal = journalPage(page)
    const saved = { ...aSession().atRevision(1).saying('Words from before').build(), id: session.id, clientSessionId: session.clientSessionId }
    await aReturnDuringASlowRead(journal, page, api, session, () => ({ ...sessionsApi.oneIs(saved), delayMs: 1_500 }))
    await journal.at.composer.click()
    const ime = await page.context().newCDPSession(page)

    await ime.send('Input.imeSetComposition', { text: 'dictated', selectionStart: 8, selectionEnd: 8 })
    await expect(journal.at.composer).toContainText('Words from before', { timeout: 5_000 })
    await ime.send('Input.imeSetComposition', { text: 'dictated words', selectionStart: 14, selectionEnd: 14 })
    await ime.send('Input.insertText', { text: 'dictated words' })

    await expect(journal.at.composer).toContainText('Words from before')
    await expect(journal.at.composer).toContainText('dictated words')
  })

  test('a reload during the read joins the keys to the entry, and does not replace it', async ({ page, api }) => {
    const session = aSession().build()
    const journal = journalPage(page)
    const saved = await aReturnDuringAFailingRead(journal, page, api, session)
    await journal.typeWithoutWaiting('Words typed early')
    api.mock(sessionsApi.oneIs(saved), sessionsApi.draftSaveWins(session, 2))

    await page.reload()

    await expect.poll(() => wordsOf(api.last('put', sessionsPaths.rawDraft(session.id))), { timeout: 10_000 }).toContain('Words typed early')
    expect(wordsOf(api.last('put', sessionsPaths.rawDraft(session.id)))).toContain('Words from before')
  })

  test('a logout during the read asks about the keys', async ({ page, api }) => {
    const unsent = unsentWords(page)
    const session = aSession().build()
    api.mock(signInPage, logoutWorks)
    const journal = journalPage(page)
    await aReturnDuringASlowRead(journal, page, api, session, (saved) => ({ ...sessionsApi.oneIs(saved), delayMs: 10_000 }))
    await journal.typeWithoutWaiting('Words typed early')

    await unsent.logOut()

    await expect(unsent.at.logoutDialog).toBeVisible()
    await unsent.answerTheLogoutDialog('Keep words and sign out')
    await unsent.reachTheSignedOutPage()
    const [key] = await unsent.keys()
    expect(await unsent.stored(key)).toContain('Words typed early')
  })
})

test.describe('a logout', () => {
  test('sends the unsent words first, and asks nothing when that works', async ({ page, api }) => {
    const unsent = unsentWords(page)
    const session = aSession().build()
    api.mock(signInPage, logoutWorks, sessionsApi.creates(session), sessionsApi.draftSaveWins(session, 1))
    const journal = journalPage(page)
    await journal.open()
    await journal.typeWithoutWaiting('Words at the last moment')

    await unsent.logOut()

    await unsent.reachTheSignedOutPage()
    expect(wordsOf(api.last('put', sessionsPaths.rawDraft(session.id)))).toContain('Words at the last moment')
    expect(await unsent.keys()).toEqual([])
    expect(await unsent.composerPointer()).toBeNull()
  })

  test('with the browser offline asks at once, and goes on when the network returns', async ({ page, api }) => {
    const unsent = unsentWords(page)
    const session = aSession().build()
    api.mock(signInPage, logoutWorks, sessionsApi.creates(session), sessionsApi.draftSaveHasNoReply(session))
    const journal = journalPage(page)
    await journal.open()

    await page.context().setOffline(true)
    await journal.typeWithoutWaiting('Words with no network')
    await unsent.logOut()

    await expect(unsent.at.logoutDialog).toBeVisible()
    await page.context().setOffline(false)
    await unsent.answerTheLogoutDialog('Keep words and sign out')
    await unsent.reachTheSignedOutPage()
    expect(await unsent.keys()).toHaveLength(1)
  })

  for (const { choice, stays } of [{ choice: 'Keep words and sign out', stays: true }, { choice: 'Discard words and sign out', stays: false }]) {
    test(`that cannot send the words asks first: ${choice}`, async ({ page, api }) => {
      const unsent = unsentWords(page)
      const session = aSession().build()
      api.mock(signInPage, logoutWorks, sessionsApi.creates(session), sessionsApi.draftSaveHasNoReply(session))
      const journal = journalPage(page)
      await journal.open()
      await journal.typeWithoutWaiting('Words with no connection')

      await unsent.logOut()

      await expect(unsent.at.logoutDialog).toBeVisible()
      expect(api.all('post', '/auth/logout')).toHaveLength(0)
      await unsent.answerTheLogoutDialog(choice)
      await unsent.reachTheSignedOutPage()
      expect(await unsent.composerPointer()).toBeNull()
      const keys = await unsent.keys()
      expect(keys).toHaveLength(stays ? 1 : 0)
      if (stays) expect(keys[0]).toContain(`recall.unsent.${actorSubject}.`)
    })
  }
})

test.describe('a setting that did not reach the Server', () => {
  test('to turn location on does not go out on the next load, because only a click in a live page turns location on', async ({ page, api }) => {
    const unsent = unsentWords(page)
    api.mock({ method: 'put', path: settingsPaths.mine, status: 503 })
    await unsent.openSettings()
    await unsent.at.locationCapture.click({ force: true })
    await expect.poll(() => api.all('put', settingsPaths.mine).length).toBeGreaterThan(0)

    api.mock(settingsApi.saves(aSetting({ locationCaptureEnabled: true })))
    const putsBefore = api.all('put', settingsPaths.mine).length
    await page.reload()

    await expect.poll(() => unsent.settingKeys()).toEqual([])
    await expect(unsent.at.locationCapture).not.toBeChecked()
    for (const put of api.all('put', settingsPaths.mine).slice(putsBefore)) expect(JSON.stringify(put.body)).not.toContain('"locationCaptureEnabled":true')
  })

  test('to turn location on, from a tab that died, is dropped at the next load', async ({ page, api }) => {
    const unsent = unsentWords(page)
    api.mock(settingsApi.are(aSetting({ timeZone: null })), { method: 'put', path: settingsPaths.mine, status: 503 })
    await unsent.leftInStorage(`recall.unsent-setting.${actorSubject}.tab-that-died.1`, JSON.stringify({
      change: { value: { locationCaptureEnabled: true }, savedAt: '2026-11-06T15:31:00.000Z' },
      attempt: null,
      asks: [],
    }))

    await unsent.openSettings()

    await expect.poll(() => unsent.settingKeys()).toEqual([])
    await expect(unsent.at.locationCapture).not.toBeChecked()
    expect(api.all('put', settingsPaths.mine)).toHaveLength(0)
  })
})

test.describe('the location of a new entry', () => {
  // A create that failed retries after its wait, and a failure of the poll must show as a failure of the poll.
  test.setTimeout(60_000)
  const place = { latitude: 41.5, longitude: -88.4 }
  const failingPut = { method: 'put' as const, path: settingsPaths.mine, status: 503 }

  const givePlace = async (context: BrowserContext) => {
    await context.grantPermissions(['geolocation'])
    await context.setGeolocation(place)
  }

  const typeOnTheJournal = async (page: Page) => {
    const journal = journalPage(page)
    // The first word asks the row, so this tab must have its answer. A read of another tab tells nothing.
    const rowAnswered = journal.aReadOfTheSettingsAnswered()
    await journal.open()
    await rowAnswered
    await journal.typeWithoutWaiting('Words at the cafe')
  }

  const createdLatitude = async (api: Api) => {
    await expect.poll(() => api.all('post', sessionsPaths.timeline).length, { timeout: 40_000 }).toBeGreaterThan(0)
    return (api.last('post', sessionsPaths.timeline)?.body as { latitude: number | null }).latitude
  }

  test('goes with the entry while capture stays on', async ({ page, context, api }) => {
    await givePlace(context)
    const session = aSession().build()
    api.mock(settingsApi.are(aSetting({ locationCaptureEnabled: true })), sessionsApi.creates(session), sessionsApi.draftSaveWins(session, 1))

    await typeOnTheJournal(page)

    expect(await createdLatitude(api)).toBe(place.latitude)
  })

  test('stays out when the person turns capture off after the first word, and the change waits', async ({ page, context, api }) => {
    await givePlace(context)
    const session = aSession().build()
    const unsent = unsentWords(page)
    api.mock(settingsApi.are(aSetting({ locationCaptureEnabled: true })), { method: 'post', path: sessionsPaths.timeline, status: 503 }, failingPut)
    await typeOnTheJournal(page)
    await expect.poll(() => api.all('post', sessionsPaths.timeline).length).toBeGreaterThan(0)

    await unsent.at.settingsLink.click()
    await unsent.at.locationCapture.click({ force: true })
    await expect(unsent.at.locationCapture).not.toBeChecked()
    api.mock(sessionsApi.creates(session), sessionsApi.draftSaveWins(session, 1))

    await expect.poll(() => (api.last('post', sessionsPaths.timeline)?.body as { latitude: number | null }).latitude, { timeout: 40_000 }).toBeNull()
  })

  test('stays out while another tab holds a change to off', async ({ page, context, api }) => {
    await givePlace(context)
    const session = aSession().build()
    const unsent = unsentWords(page)
    api.mock(settingsApi.are(aSetting({ locationCaptureEnabled: true })), failingPut, sessionsApi.creates(session), sessionsApi.draftSaveWins(session, 1))
    await unsent.openSettings()
    await unsent.at.locationCapture.click({ force: true })
    await expect.poll(() => unsent.settingKeys()).not.toEqual([])

    const secondTab = await context.newPage()
    await typeOnTheJournal(secondTab)

    expect(await createdLatitude(api)).toBeNull()
    expect(await unsentWords(secondTab).at.noLocationNotice.count()).toBe(0)
  })

  test('stays out with a notice while an answer about a change to off waits', async ({ page, context, api }) => {
    await givePlace(context)
    const session = aSession().build()
    const unsent = unsentWords(page)
    api.mock(settingsApi.are(aSetting({ locationCaptureEnabled: true })), sessionsApi.creates(session), sessionsApi.draftSaveWins(session, 1))
    await unsent.leftInStorage(`recall.unsent-setting.${actorSubject}.tab-that-died.1`, JSON.stringify({
      change: null,
      attempt: null,
      asks: [{ field: 'locationCaptureEnabled', yours: false, now: true }],
    }))

    await typeOnTheJournal(page)

    expect(await createdLatitude(api)).toBeNull()
    await expect(unsent.at.noLocationNotice).toBeVisible()
  })

  test('stays out at the first word when a tab that died left a change to off', async ({ page, context, api }) => {
    await givePlace(context)
    const session = aSession().build()
    const unsent = unsentWords(page)
    api.mock(settingsApi.are(aSetting({ locationCaptureEnabled: true })), failingPut, sessionsApi.creates(session), sessionsApi.draftSaveWins(session, 1))
    await unsent.leftInStorage(`recall.unsent-setting.${actorSubject}.tab-that-died.1`, JSON.stringify({
      change: { value: { locationCaptureEnabled: false }, savedAt: '2026-11-06T15:31:00.000Z' },
      attempt: null,
      asks: [],
    }))

    await typeOnTheJournal(page)

    expect(await createdLatitude(api)).toBeNull()
  })
})

test.describe('words that the browser has no room to keep', () => {
  test('show a notice at once with a copy, and the page warns before it closes', async ({ page, api }) => {
    const unsent = unsentWords(page)
    const session = aSession().build()
    api.mock(sessionsApi.creates(session), { method: 'put', path: sessionsPaths.rawDraft(session.id), status: 503 })
    const journal = journalPage(page)
    await journal.open()
    await unsent.fillTheStorage()

    await journal.typeWithoutWaiting('Words with no room')

    await expect(unsent.at.onlyInMemory).toBeVisible()
    await expect(unsent.at.onlyInMemory).toContainText('Words with no room')
    await expect(unsent.at.copyOnlyInMemory).toBeVisible()
    expect(await unsent.warnsAtAClose()).toBe(true)
  })

  test('at a logout, the dialog does not offer to keep them', async ({ page, api }) => {
    const unsent = unsentWords(page)
    const session = aSession().build()
    api.mock(sessionsApi.creates(session), { method: 'put', path: sessionsPaths.rawDraft(session.id), status: 503 })
    const journal = journalPage(page)
    await journal.open()
    await unsent.fillTheStorage()
    await journal.typeWithoutWaiting('Words with no room')
    await expect(unsent.at.onlyInMemory).toBeVisible()

    await unsent.logOut()

    await expect(unsent.at.logoutDialog).toContainText('no room', { timeout: 10_000 })
    await expect(unsent.at.logoutChoice('Keep words and sign out')).toHaveCount(0)
    await unsent.answerTheLogoutDialog('Stay signed in')
    await expect(unsent.at.onlyInMemory).toBeVisible()
    expect(api.all('post', '/auth/logout')).toHaveLength(0)
    await expect(unsent.at.signOutDidNotFinish).toHaveCount(0)
  })

  test('at a sign-out in another tab, the tab shows them alone with a copy, and leaves with no browser prompt when the person says so', async ({ page, api }) => {
    const unsent = unsentWords(page)
    const session = aSession().build()
    api.mock(sessionsApi.creates(session), { method: 'put', path: sessionsPaths.rawDraft(session.id), status: 503 })
    const journal = journalPage(page)
    await journal.open()
    await unsent.fillTheStorage()
    await journal.typeWithoutWaiting('Words with no room')
    await expect(unsent.at.onlyInMemory).toBeVisible()
    api.mock(authApi.loginOpens())
    const dialogs = unsent.countTheDialogs()

    await unsent.signOutInAnotherTab()

    await expect(unsent.at.leftAtSignOut).toContainText('Words with no room')
    await expect(unsent.at.copyLeftAtSignOut).toBeVisible()
    await expect(unsent.at.onlyInMemory).toHaveCount(0)
    expect(page.url()).not.toContain('signedOut=1')
    await unsent.at.leaveThePage.click()
    await expect.poll(() => page.url()).toContain('signedOut=1')
    expect(dialogs()).toBe(0)
  })

  const holdWordsOnlyInMemory = async (page: Page, api: Api) => {
    const unsent = unsentWords(page)
    const session = aSession().build()
    api.mock(sessionsApi.creates(session), { method: 'put', path: sessionsPaths.rawDraft(session.id), status: 503 })
    const journal = journalPage(page)
    await journal.open()
    await unsent.fillTheStorage()
    await journal.typeWithoutWaiting('Words with no room')
    await expect(unsent.at.onlyInMemory).toBeVisible()
    return { unsent, session }
  }

  test('at a logout, words typed while the logout call runs show alone, and the tab warns before it closes', async ({ page, api }) => {
    const unsent = unsentWords(page)
    const session = aSession().build()
    let answerTheLogout!: () => void
    const logoutAnswers = new Promise<void>((settle) => (answerTheLogout = settle))
    api.mock(sessionsApi.creates(session), { method: 'put', path: sessionsPaths.rawDraft(session.id), status: 503 }, authApi.loginOpens(), { method: 'post', path: authPaths.logout, status: 204, heldUntil: logoutAnswers })
    const journal = journalPage(page)
    await journal.open()
    await journal.typeWithoutWaiting('Words in a key')
    await expect.poll(() => unsent.keys()).toHaveLength(1)
    await unsent.fillTheStorage()
    await unsent.logOut()
    await unsent.answerTheLogoutDialog('Keep words and sign out')
    await expect.poll(() => api.all('post', authPaths.logout).length).toBe(1)

    await journal.typeWithoutWaiting(' and words during the logout')
    await expect(unsent.at.onlyInMemory).toBeVisible()
    answerTheLogout()

    const screen = unsent.at.leftAs('You signed out')
    await expect(screen).toContainText('Words in a key')
    await expect(screen).toContainText('words during the logout')
    expect(page.url()).not.toContain('signedOut=1')
    expect(await unsent.warnsAtAClose()).toBe(true)
    await unsent.at.leaveThePage.click()
    await unsent.reachTheSignedOutPage()
  })

  test('when the session ends with no message, the tab shows them alone, and keeps the way back to the page', async ({ page, api }) => {
    const { unsent, session } = await holdWordsOnlyInMemory(page, api)
    api.mock(authApi.loginOpens(), { method: 'get', path: authPaths.me, status: 204 }, { method: 'put', path: sessionsPaths.rawDraft(session.id), status: 401 })

    await unsent.signalThatTheConnectionReturned()

    await expect(unsent.at.leftAs('Your session ended')).toContainText('Words with no room', { timeout: 15_000 })
    expect(page.url()).not.toContain('/auth/login')
    await unsent.at.leaveThePage.click()
    await expect.poll(() => page.url()).toContain('/auth/login?returnUrl=')
  })

  test('when the session ends while the logout dialog asks about them, the dialog closes and the tab shows them alone', async ({ page, api }) => {
    const { unsent, session } = await holdWordsOnlyInMemory(page, api)
    await unsent.logOut()
    await expect(unsent.at.logoutDialog).toContainText('no room', { timeout: 10_000 })
    api.mock(authApi.loginOpens(), { method: 'get', path: authPaths.me, status: 204 }, { method: 'put', path: sessionsPaths.rawDraft(session.id), status: 401 })

    await unsent.signalThatTheConnectionReturned()

    await expect(unsent.at.leftAs('Your session ended')).toContainText('Words with no room', { timeout: 15_000 })
    await expect(unsent.at.logoutDialog).toHaveCount(0)
    expect(await unsent.warnsAtAClose()).toBe(true)
    await unsent.at.leaveThePage.click()
    await expect.poll(() => page.url()).toContain('/auth/login?returnUrl=')
    expect(api.all('post', authPaths.logout)).toHaveLength(0)
  })

  test('when a restored page finds its session ended, the page shows them alone', async ({ page, api }) => {
    const { unsent } = await holdWordsOnlyInMemory(page, api)
    api.mock(authApi.loginOpens(), { method: 'get', path: authPaths.me, status: 204 })

    await unsent.restoreThePage()

    await expect(unsent.at.leftAs('Your session ended')).toContainText('Words with no room')
    expect(await unsent.pageShows()).toBe(true)
  })

  test('when a restored page finds a newer session of the same person, the page shows them alone, and loads again at Leave', async ({ page, api }) => {
    const { unsent } = await holdWordsOnlyInMemory(page, api)
    const pageOfTheWords = page.url()
    api.mock({ method: 'get', path: authPaths.me, status: 200, body: { sub: actorSubject, name: 'Bob Smith', sid: 'a-newer-session', client_kind: 'web' } })

    await unsent.restoreThePage()

    await expect(unsent.at.leftAs('Your session ended')).toContainText('Words with no room')
    expect(await unsent.pageShows()).toBe(true)
    await unsent.at.leaveThePage.click()
    await expect(unsent.at.leftAs('Your session ended')).toHaveCount(0)
    expect(page.url()).toBe(pageOfTheWords)
  })

  test('when another person signs in, the tab drops them at once, and the next page says that they are gone', async ({ page, api }) => {
    const { unsent } = await holdWordsOnlyInMemory(page, api)
    api.mock(signInPage)

    await unsent.signInOfAnotherPersonInAnotherTab()

    await expect.poll(() => page.url()).toContain('/auth/login?anotherPerson=1&wordsLost=1')
    await expect(unsent.at.wholePage).not.toContainText('Words with no room')
  })
})

test.describe('a save that waits after Done', () => {
  test('has a notice on each page until the save works', async ({ page, api }) => {
    const unsent = unsentWords(page)
    const session = aSession().build()
    api.mock(sessionsApi.creates(session), { method: 'put', path: sessionsPaths.rawDraft(session.id), status: 503 })
    const journal = journalPage(page)
    await journal.open()
    await journal.typeWithoutWaiting('Words after Done')
    await journal.finishFromTheKeyboard()

    await journal.goToSettings()

    await expect(unsent.at.waiting).toContainText('Words after Done')
    await expect(unsent.at.copyWaiting).toBeVisible()
    await expect(unsent.at.discardWaiting).toBeVisible()

    api.mock(sessionsApi.draftSaveWins(session, 1))
    await unsent.signalThatTheConnectionReturned()

    await expect(unsent.at.waiting).toHaveCount(0)
  })
})

test.describe('the editor at the moment that it opens other words', () => {
  test('stays the same editor at Done, and the first key of the next entry lands in it', async ({ page, api }) => {
    const session = aSession().build()
    api.mock(sessionsApi.creates(session), sessionsApi.draftSaveWins(session, 1))
    const journal = journalPage(page)
    await journal.open()
    await journal.typeWithoutWaiting('First entry')
    await journal.markTheEditor()

    await page.keyboard.press('ControlOrMeta+Enter')
    await page.keyboard.type('Next')

    expect(await journal.isTheSameEditor()).toBe(true)
    await expect(journal.at.composer).toHaveText('Next')
  })

  test('undo after Done does not bring back the words of the last entry', async ({ page, api }) => {
    const session = aSession().build()
    api.mock(sessionsApi.creates(session), sessionsApi.draftSaveWins(session, 1))
    const journal = journalPage(page)
    await journal.open()
    await journal.typeWithoutWaiting('First entry')

    await page.keyboard.press('ControlOrMeta+Enter')
    await page.keyboard.press('ControlOrMeta+z')

    await expect(journal.at.composer).not.toContainText('First entry')
  })
})
