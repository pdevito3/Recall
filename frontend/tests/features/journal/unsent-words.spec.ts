import type { Page } from '@playwright/test'
import type { SessionDto } from '@/api/generated/models/types/session-dto'
import { authApi } from '@tests/support/api/auth-api'
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
    api.mock(sessionsApi.draftSaveWins(session, 2))
    const key = await unsent.leftInTheOutbox({ words: 'Words of an earlier load', sessionId: session.id })

    await unsent.openSettings()

    await expect.poll(() => wordsOf(api.last('put', sessionsPaths.rawDraft(session.id)))).toContain('Words of an earlier load')
    await expect.poll(() => unsent.stored(key)).toBeNull()
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
    api.mock(sessionsApi.draftSaveWins(session, 2))
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

  test('does not say Saved for words that lost to a newer save', async ({ page, api }) => {
    const session = aSession().build()
    api.mock(sessionsApi.creates(session), sessionsApi.oneIs(session), sessionsApi.draftSaveLoses(session, { draftRevision: 3, latestRevision: 4 }))
    const journal = journalPage(page)
    await journal.open()

    await journal.write('Words that lose')

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

    await expect(journal.at.status('The save waits for a connection')).toBeVisible()
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
  test('goes out on the next load', async ({ page, api }) => {
    const unsent = unsentWords(page)
    api.mock({ method: 'put', path: settingsPaths.mine, status: 503 })
    await unsent.openSettings()
    await unsent.at.locationCapture.click({ force: true })
    await expect.poll(() => api.all('put', settingsPaths.mine).length).toBeGreaterThan(0)

    api.mock(settingsApi.saves(aSetting({ locationCaptureEnabled: true })))
    await page.reload()

    await expect.poll(() => JSON.stringify(api.last('put', settingsPaths.mine)?.body)).toContain('"locationCaptureEnabled":true')
    await expect(unsent.at.locationCapture).toBeChecked()
    await expect.poll(() => unsent.settingKeys()).toEqual([])
  })

  test('is not lost to the time zone seed of the next load', async ({ page, api }) => {
    const unsent = unsentWords(page)
    api.mock(settingsApi.are(aSetting({ timeZone: null })), { method: 'put', path: settingsPaths.mine, status: 503 })
    await unsent.leftInStorage(`recall.unsent-setting.${actorSubject}`, JSON.stringify({
      change: { value: { locationCaptureEnabled: true }, savedAt: '2026-11-06T15:31:00.000Z' },
      attempt: null,
      asks: [],
    }))

    await unsent.openSettings()

    await expect.poll(() => api.all('put', settingsPaths.mine).length).toBeGreaterThan(0)
    for (const put of api.all('put', settingsPaths.mine)) expect(JSON.stringify(put.body)).toContain('"locationCaptureEnabled":true')
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

  test('at a sign-out in another tab, the tab leaves with no question, so that no hidden page stays', async ({ page, api }) => {
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

    await expect.poll(() => page.url()).toContain('signedOut=1')
    expect(dialogs()).toBe(0)
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
