import { modelProviderPaths, modelProvidersApi } from '@tests/support/api/model-providers-api'
import { aPerson, peopleApi } from '@tests/support/api/people-api'
import { sessionsApi, sessionsPaths } from '@tests/support/api/sessions-api'
import { aSession } from '@tests/support/builders/session-builder'
import { expect, test } from '@tests/support/fixtures'
import { entryPage } from './journal.tasks'

test.describe('the Cleanup of an entry', () => {
  test.beforeEach(({ api }) => {
    api.mock(modelProvidersApi.activeIs('Home lab'))
  })

  test('a run shows its progress, and its result shows in the Cleaned tab with no reload', async ({ page, api }) => {
    const entry = aSession().saying('um I met sam today').cleaned('Old clean words.')
    const running = entry.cleanupRunning().build()
    api.mock(sessionsApi.oneIs(entry.build()), sessionsApi.cleanupStarts(running))
    const at = entryPage(page)
    await at.open(running.id)
    await at.showCleaned()
    await at.markThePage()

    api.mock(sessionsApi.oneIs(running))
    await at.runCleanup(running.id)
    await expect(at.at.cleanupStatus('You can close this page')).toBeVisible()
    await expect(at.at.runCleanup).toBeDisabled()
    await expect(at.at.stopCleanup).toBeVisible()
    await expect(at.at.textOfThePage('This Cleanup sends your words to Home lab.')).toBeVisible()
    api.mock(sessionsApi.oneIs(entry.cleaned('I met Sam today.', 2).build()))

    await expect(at.at.cleanedEditor).toContainText('I met Sam today.')
    await expect(at.at.cleanupStatus('Cleaned is current.')).toBeVisible()
    await expect(at.at.textOfThePage('Cleaned changed in a Cleanup, another tab, or another device.')).toBeVisible()
    await expect(at.at.stopCleanup).toHaveCount(0)
    expect(await at.isTheSamePage()).toBe(true)
  })

  test('the first Cleanup of an entry adds the Cleaned tab, and the words of the person stay', async ({ page, api }) => {
    const entry = aSession().saying('um I met sam today')
    const running = entry.cleanupRunning().build()
    api.mock(sessionsApi.oneIs(entry.build()), sessionsApi.cleanupStarts(running))
    const at = entryPage(page)
    await at.open(running.id)
    await expect(at.at.cleanedTab).toHaveCount(0)

    api.mock(sessionsApi.oneIs(running))
    await at.runCleanup(running.id)
    api.mock(sessionsApi.oneIs(entry.cleaned('I met Sam today.').build()))
    await expect(at.at.cleanedTab).toBeVisible()

    await expect(at.at.words('um I met sam today')).toBeVisible()
    await at.showCleaned()
    await expect(at.at.cleanedEditor).toContainText('I met Sam today.')
    expect(api.all('put', sessionsPaths.rawDraft(running.id))).toHaveLength(0)
  })

  test('a person can leave a run, and a later visit finds Cleaned', async ({ page, api }) => {
    const entry = aSession().saying('um I met sam today')
    const running = entry.cleanupRunning().build()
    api.mock(sessionsApi.oneIs(entry.build()), sessionsApi.cleanupStarts(running))
    const at = entryPage(page)
    await at.open(running.id)
    api.mock(sessionsApi.oneIs(running))
    await at.runCleanup(running.id)
    await expect(at.at.cleanupStatus('You can close this page')).toBeVisible()

    await page.goto('/about')
    api.mock(sessionsApi.oneIs(entry.cleaned('I met Sam today.').build()))
    await at.open(running.id)

    await expect(at.at.cleanupStatus('Cleaned is current.')).toBeVisible()
    await expect(at.at.words('um I met sam today')).toBeVisible()
    await at.showCleaned()
    await expect(at.at.cleanedEditor).toContainText('I met Sam today.')
  })

  test('a Member reads the name of the provider that gets the words, and the page asks for no list of providers', async ({ page, api }) => {
    const session = aSession().build()
    api.mock(sessionsApi.oneIs(session))
    const at = entryPage(page)

    await at.open(session.id)

    await expect(at.at.wordsGoTo('Home lab')).toBeVisible()
    await expect(at.at.runCleanup).toBeEnabled()
    expect(api.last('get', modelProviderPaths.active)).toBeDefined()
    expect(api.last('get', modelProviderPaths.list)).toBeUndefined()
  })

  test('with no active provider, the page names none and a run is off', async ({ page, api }) => {
    const session = aSession().build()
    api.mock(sessionsApi.oneIs(session), modelProvidersApi.activeIs(null))
    const at = entryPage(page)

    await at.open(session.id)

    await expect(at.at.noProviderNote).toBeVisible()
    await expect(at.at.runCleanup).toBeDisabled()
    await expect(at.at.wordsGoToAnyProvider).toHaveCount(0)
  })

  test('a person stops a running Cleanup, the page says so, and a new run is open', async ({ page, api }) => {
    const entry = aSession().saying('um I met sam today')
    const running = entry.cleanupRunning().build()
    const stopped = entry.cleanupFailed('Stopped').build()
    api.mock(sessionsApi.oneIs(running), sessionsApi.cleanupStops(stopped))
    const at = entryPage(page)
    await at.open(running.id)
    await expect(at.at.stopCleanup).toBeVisible()

    // The read says Stopped only after the stop. A poll of the running Cleanup before the click would end it with no click.
    await at.stopCleanup(running.id)
    api.mock(sessionsApi.oneIs(stopped))

    await expect(at.at.cleanupStatus('You stopped the last Cleanup.')).toBeVisible()
    await expect(at.at.cleanupAlert('You stopped')).toHaveCount(0)
    await expect(at.at.stopCleanup).toHaveCount(0)
    await expect(at.at.runCleanup).toBeEnabled()
    await expect(at.at.runCleanup).toHaveText('Run Cleanup again')
    await expect(at.at.words('um I met sam today')).toBeVisible()
  })

  test('a run turns Run Cleanup off, so the focus goes to Stop Cleanup, and a stop gives it back to Run Cleanup', async ({ page, api }) => {
    const entry = aSession().saying('um I met sam today')
    const running = entry.cleanupRunning().build()
    const stopped = entry.cleanupFailed('Stopped').build()
    api.mock(sessionsApi.oneIs(entry.build()), sessionsApi.cleanupStarts(running), sessionsApi.cleanupStops(stopped))
    const at = entryPage(page)
    await at.open(running.id)

    api.mock(sessionsApi.oneIs(running))
    await at.runCleanup(running.id)
    await expect(at.at.runCleanup).toBeDisabled()
    await expect(at.at.stopCleanup).toBeFocused()

    await at.stopCleanup(running.id)
    api.mock(sessionsApi.oneIs(stopped))
    await expect(at.at.stopCleanup).toHaveCount(0)
    await expect(at.at.runCleanup).toBeFocused()
  })

  test('a finished run has no stop button', async ({ page, api }) => {
    const clean = aSession().cleaned('Done.').build()
    api.mock(sessionsApi.oneIs(clean))
    const at = entryPage(page)

    await at.open(clean.id)

    await expect(at.at.runCleanup).toHaveText('Run Cleanup again')
    await expect(at.at.stopCleanup).toHaveCount(0)
  })

  test('a refused start shows why', async ({ page, api }) => {
    const session = aSession().build()
    const message = 'An Admin must connect a model provider and pick a model first.'
    api.mock(sessionsApi.oneIs(session), sessionsApi.cleanupIsRefused(session, message))
    const at = entryPage(page)
    await at.open(session.id)

    await at.runCleanup(session.id)

    await expect(at.at.cleanupAlert(message)).toBeVisible()
    await expect(at.at.toasts).toHaveCount(0)
  })

  test('a failed run shows a closed reason that names the page of the providers', async ({ page, api }) => {
    const failed = aSession().cleanupFailed('ProviderRefused').build()
    api.mock(sessionsApi.oneIs(failed))
    const at = entryPage(page)

    await at.open(failed.id)

    await expect(at.at.cleanupAlert('The model provider refused the request. An Admin can test the connection on the Model providers page.')).toBeVisible()
    await expect(at.at.cleanupStatus('The last Cleanup did not finish. Your words did not change.')).toBeVisible()
  })

  test('a failed new run shows its reason while the words are newer than Cleaned', async ({ page, api }) => {
    const failed = aSession().cleaned('Done.').staleAfterAFailedRun('ProviderUnavailable').build()
    api.mock(sessionsApi.oneIs(failed))
    const at = entryPage(page)

    await at.open(failed.id)

    await expect(at.at.cleanupAlert('The model provider did not answer.')).toBeVisible()
    await expect(at.at.cleanupStatus('Your words changed after the last Cleanup.')).toBeVisible()
  })

  test('hand edits warn before a new run', async ({ page, api }) => {
    const edited = aSession().cleaned('Mine.').cleanedByHand().build()
    api.mock(sessionsApi.oneIs(edited))
    const at = entryPage(page)

    await at.open(edited.id)

    await expect(at.at.cleanupStatus('You edited Cleaned by hand after the last Cleanup.')).toBeVisible()
    await expect(at.at.textOfThePage('A new Cleanup replaces Cleaned, and your edit stays as a Revision.')).toBeVisible()
  })

  test('a tag leaves the choice of the Person to the server, and the Person shows in the tags with no reload', async ({ page, api }) => {
    const entry = aSession().cleaned('I met Sam today.')
    const session = entry.proposing('Sam').build()
    api.mock(sessionsApi.oneIs(session), sessionsApi.proposalAnswered(entry.mentioning('per_1').build()))
    const at = entryPage(page)
    await at.open(session.id)
    await at.markThePage()

    api.mock(peopleApi.listIs([aPerson('per_1', 'Sam')]))
    await at.tagPerson(session.id, 'Sam')

    await expect(at.at.tagPerson('Sam')).toHaveCount(0)
    await expect(at.at.tagsLine('@Sam')).toBeVisible()
    expect(api.last('post', sessionsPaths.respondToProposal(session.id))?.body).toMatchObject({ label: 'Sam', approve: true, createNew: false })
    expect(await at.isTheSamePage()).toBe(true)
  })

  test('a proposal of two people of one name shows the details of each, and a Tag tags the chosen one', async ({ page, api }) => {
    const entry = aSession().cleaned('I met Jane today.')
    const session = entry.proposingOneOf('Jane', 'per_1', 'per_2').build()
    api.mock(
      sessionsApi.oneIs(session),
      peopleApi.listIs([aPerson('per_1', 'Jane', { fullName: 'Jane Park', notes: "Sam's teacher" }), aPerson('per_2', 'Jane', { fullName: 'Jane Doe' })]),
      sessionsApi.proposalAnswered(entry.mentioning('per_2').build()),
    )
    const at = entryPage(page)
    await at.open(session.id)
    await expect(at.at.textOfThePage("Sam's teacher")).toBeVisible()
    await expect(at.at.tagPerson('Jane')).toHaveCount(0)

    await at.tagPerson(session.id, 'Jane Doe')

    await expect(at.at.peopleNamed('Jane')).toHaveCount(0)
    expect(api.last('post', sessionsPaths.respondToProposal(session.id))?.body).toEqual({
      label: 'Jane',
      approve: true,
      createNew: false,
      bindToPersonId: 'per_2',
    })
  })

  test('a proposal of a name that a person has can add a new person of that name', async ({ page, api }) => {
    const entry = aSession().cleaned('I met Jane today.')
    const session = entry.proposingOneOf('Jane', 'per_1').build()
    api.mock(
      sessionsApi.oneIs(session),
      peopleApi.listIs([aPerson('per_1', 'Jane', { fullName: 'Jane Park' })]),
      sessionsApi.proposalAnswered(entry.mentioning('per_9').build()),
    )
    const at = entryPage(page)
    await at.open(session.id)
    await expect(at.at.textOfThePage('Jane Park')).toBeVisible()

    await at.addNewPerson(session.id, 'Jane')

    await expect(at.at.addNewPerson('Jane')).toHaveCount(0)
    expect(api.last('post', sessionsPaths.respondToProposal(session.id))?.body).toEqual({ label: 'Jane', approve: true, createNew: true })
  })

  test('a tag that the server refuses because a second person has the name now reads the entry again and offers both', async ({ page, api }) => {
    const askWhich = 'Two or more People have this name. Choose the person to tag, or add a new person.'
    const entry = aSession().cleaned('I met Jane today.')
    const session = entry.proposingOneOf('Jane', 'per_1').build()
    const park = aPerson('per_1', 'Jane', { fullName: 'Jane Park' })
    api.mock(sessionsApi.oneIs(session), peopleApi.listIs([park]), sessionsApi.proposalIsRefused(session, askWhich))
    const at = entryPage(page)
    await at.open(session.id)
    await expect(at.at.textOfThePage('Jane Park')).toBeVisible()

    const doe = aPerson('per_2', 'Jane', { fullName: 'Jane Doe' })
    api.mock(sessionsApi.oneIs(entry.proposingOneOf('Jane', 'per_1', 'per_2').build()), peopleApi.listIs([park, doe]), peopleApi.oneIs(doe))
    await at.tagPerson(session.id, 'Jane')

    await expect(at.at.cleanupAlert(askWhich)).toBeVisible()
    await expect(at.at.peopleNamed('Jane')).toBeVisible()
    await expect(at.at.tagPerson('Jane Doe')).toBeVisible()
    expect(api.last('post', sessionsPaths.respondToProposal(session.id))?.body).toEqual({
      label: 'Jane',
      approve: true,
      createNew: false,
      bindToPersonId: 'per_1',
    })
  })

  test('a refused tag shows the words of the server once, and no toast', async ({ page, api }) => {
    const message = 'A tag of this name makes Cleaned too long. Choose Do not tag, or run a new Cleanup.'
    const session = aSession().cleaned('I met Sam today.').proposing('Sam').build()
    api.mock(sessionsApi.oneIs(session), sessionsApi.proposalIsRefused(session, message))
    const at = entryPage(page)
    await at.open(session.id)

    await at.tagPerson(session.id, 'Sam')

    await expect(at.at.cleanupAlert(message)).toBeVisible()
    await expect(at.at.textOfThePage(message)).toHaveCount(1)
    await expect(at.at.toasts).toHaveCount(0)
  })

  test('an accepted Mood shows in the tags with no reload', async ({ page, api }) => {
    const entry = aSession().cleaned('A calm day.')
    const session = entry.suggesting({ kind: 'Mood', value: 'Calm' }).build()
    api.mock(sessionsApi.oneIs(session), sessionsApi.suggestionAccepted(entry.tagged({ moods: ['Calm'] }).build()))
    const at = entryPage(page)
    await at.open(session.id)
    await expect(at.at.tagsLine('Calm')).toHaveCount(0)

    await at.acceptSuggestion(session.id, 'Calm')

    await expect(at.at.acceptSuggestion('Calm')).toHaveCount(0)
    await expect(at.at.tagsLine('Calm')).toBeVisible()
  })

  test('the synopsis, Cleaned, and the state of the Cleanup each show once on the page', async ({ page, api }) => {
    const session = aSession().cleaned('Clean words of the day.').withSynopsis('A walk with Sam.').build()
    api.mock(sessionsApi.oneIs(session))
    const at = entryPage(page)
    await at.open(session.id)

    await at.showCleaned()

    await expect(at.at.synopsis('A walk with Sam.')).toHaveCount(1)
    await expect(at.at.textOfThePage('Clean words of the day.')).toHaveCount(1)
    await expect(at.at.cleanedEditor).toContainText('Clean words of the day.')
    await expect(at.at.textOfThePage(/Cleaned is current|cleaned version is up to date/)).toHaveCount(1)
  })

  test('the Cleanup is a section of the page between the history and the tags, with a heading like theirs', async ({ page, api }) => {
    const session = aSession().cleaned('Done.').build()
    api.mock(sessionsApi.oneIs(session))
    const at = entryPage(page)

    await at.open(session.id)
    await expect(at.at.cleanupHeading).toBeVisible()

    const [history, cleanup, tags] = await Promise.all([at.at.historyHeading, at.at.cleanupHeading, at.at.tagsHeading].map((heading) => heading.boundingBox()))
    expect(history!.y).toBeLessThan(cleanup!.y)
    expect(cleanup!.y).toBeLessThan(tags!.y)
    expect(cleanup!.height).toBe(tags!.height)
    expect(cleanup!.x).toBe(tags!.x)
  })
})

test.describe('the Cleanup of an entry on a 390 px phone', () => {
  test.use({ viewport: { width: 390, height: 800 } })

  test.beforeEach(({ api }) => {
    api.mock(modelProvidersApi.activeIs('Home lab'))
  })

  test('each control of the Cleanup is 44 px high and wide, with no sideways scroll', async ({ page, api }) => {
    const session = aSession().cleaned('Done.').suggesting({ kind: 'Mood', value: 'Calm' }).proposing('Ann').build()
    api.mock(sessionsApi.oneIs(session))
    const at = entryPage(page)

    await at.open(session.id)
    await expect(at.at.tagPerson('Ann')).toBeVisible()

    for (const control of await at.at.cleanupControls.all()) {
      const box = await control.boundingBox()
      expect(Math.min(box!.width, box!.height), (await control.textContent()) ?? '').toBeGreaterThanOrEqual(44)
    }
    expect(await at.at.pageOverflow()).toBe(0)
  })

  test('a short suggestion and a short name keep their choices on their line, as each row does', async ({ page, api }) => {
    const session = aSession().cleaned('Done.').suggesting({ kind: 'Mood', value: 'Calm' }).proposing('Sam').build()
    api.mock(sessionsApi.oneIs(session))
    const at = entryPage(page)

    await at.open(session.id)
    await expect(at.at.tagPerson('Sam')).toBeVisible()

    const middleOf = async (box: Promise<{ y: number; height: number } | null>) => {
      const { y, height } = (await box)!
      return y + height / 2
    }
    const choiceAndWords = [
      [at.at.acceptSuggestion('Calm'), at.at.textOfThePage('Calm')],
      [at.at.tagPerson('Sam'), at.at.textOfThePage('Sam')],
    ] as const
    for (const [choice, words] of choiceAndWords) {
      expect(Math.abs((await middleOf(choice.boundingBox())) - (await middleOf(words.boundingBox())))).toBeLessThanOrEqual(2)
    }
  })

  test('a long Topic and a long Person name take the width of the page on each phone', async ({ page, api }) => {
    const topic = 'a long topic that a person keeps for each of the things that a week holds'
    const person = 'a long name of a person that a model takes from each of the things in a week'
    const session = aSession().cleaned('Done.').suggesting({ kind: 'Topic', value: topic }).proposing(person).build()
    api.mock(sessionsApi.oneIs(session))
    const at = entryPage(page)

    await at.open(session.id)
    await expect(at.at.textOfThePage(topic)).toBeVisible()

    for (const width of [390, 430]) {
      await page.setViewportSize({ width, height: 800 })
      expect((await at.at.textOfThePage(topic).boundingBox())?.height, `the Topic at ${width} px`).toBeLessThanOrEqual(60)
      expect((await at.at.textOfThePage(person).boundingBox())?.height, `the Person at ${width} px`).toBeLessThanOrEqual(60)
      expect(await at.at.pageOverflow()).toBe(0)
    }
  })
})

test.describe('the Cleanup of an entry on a 320 px phone', () => {
  test.use({ viewport: { width: 320, height: 640 } })

  test.beforeEach(({ api }) => {
    api.mock(modelProvidersApi.activeIs('Home lab'))
  })

  test('a short suggestion stays on one line, with no sideways scroll', async ({ page, api }) => {
    const session = aSession().cleaned('Done.').suggesting({ kind: 'Mood', value: 'Calm' }).build()
    api.mock(sessionsApi.oneIs(session))
    const at = entryPage(page)

    await at.open(session.id)

    await expect(at.at.textOfThePage('Calm')).toBeVisible()
    expect((await at.at.textOfThePage('Calm').boundingBox())?.height).toBeLessThan(24)
    expect(await at.at.pageOverflow()).toBe(0)
  })

  test('the two choices of a row stay on one line', async ({ page, api }) => {
    const session = aSession().cleaned('Done.').suggesting({ kind: 'Mood', value: 'Calm' }).proposing('Sam').build()
    api.mock(sessionsApi.oneIs(session))
    const at = entryPage(page)

    await at.open(session.id)
    await expect(at.at.rejectSuggestion('Calm')).toBeVisible()

    expect((await at.at.rejectSuggestion('Calm').boundingBox())?.y).toBe((await at.at.acceptSuggestion('Calm').boundingBox())?.y)
    expect((await at.at.doNotTagPerson('Sam').boundingBox())?.y).toBe((await at.at.tagPerson('Sam').boundingBox())?.y)
    expect(await at.at.pageOverflow()).toBe(0)
  })

  test('the choices of a proposal of two people with long details stay in the page', async ({ page, api }) => {
    const session = aSession().cleaned('Done.').proposingOneOf('Jane', 'per_1', 'per_2').build()
    api.mock(
      sessionsApi.oneIs(session),
      peopleApi.listIs([aPerson('per_1', 'Jane', { fullName: 'P'.repeat(200), notes: 'n'.repeat(1000) }), aPerson('per_2', 'Jane', { fullName: 'Jane Doe' })]),
    )
    const at = entryPage(page)

    await at.open(session.id)
    await expect(at.at.tagPerson('Jane Doe')).toBeVisible()
    await at.at.tagPerson('P'.repeat(200)).scrollIntoViewIfNeeded()

    await expect(at.at.tagPerson('P'.repeat(200))).toBeInViewport({ ratio: 1 })
    expect((await at.at.doNotTagPerson('Jane').boundingBox())?.y).toBe((await at.at.addNewPerson('Jane').boundingBox())?.y)
    expect(await at.at.pageOverflow()).toBe(0)
  })

  test('a run with a long provider name has no sideways scroll', async ({ page, api }) => {
    const running = aSession().cleaned('Done.').cleanupRunning().build()
    api.mock(sessionsApi.oneIs(running), modelProvidersApi.activeIs('a-provider-name-with-no-space-that-is-longer-than-a-phone'))
    const at = entryPage(page)

    await at.open(running.id)
    await expect(at.at.stopCleanup).toBeVisible()

    await expect(at.at.textOfThePage('a-provider-name-with-no-space-that-is-longer-than-a-phone')).toBeVisible()
    expect(await at.at.pageOverflow()).toBe(0)
  })
})
