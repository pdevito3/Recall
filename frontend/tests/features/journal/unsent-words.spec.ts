import { aSetting, settingsApi, settingsPaths } from '@tests/support/api/settings-api'
import { sessionsApi, sessionsPaths } from '@tests/support/api/sessions-api'
import type { MockRoute } from '@tests/support/api/mock-route'
import { actorSubject } from '@tests/support/auth/actors'
import { aProblem } from '@tests/support/builders/problem-details-builder'
import { aSession } from '@tests/support/builders/session-builder'
import { expect, test } from '@tests/support/fixtures'
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

  test('sends each entry that it can read, and leaves an entry that it cannot read', async ({ page, api }) => {
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
    expect(await unsent.stored(literalNull)).toBe('null')
    expect(await unsent.stored(broken)).toBe('{broken')
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
      value: { timeZone: null, locationCaptureEnabled: true, requirePeopleProposalApproval: false },
      savedAt: '2026-11-06T15:31:00.000Z',
    }))

    await unsent.openSettings()

    await expect.poll(() => api.all('put', settingsPaths.mine).length).toBeGreaterThan(0)
    for (const put of api.all('put', settingsPaths.mine)) expect(JSON.stringify(put.body)).toContain('"locationCaptureEnabled":true')
  })
})
