import { membershipPaths } from '@tests/support/api/membership-api'
import { aProvider, aSession, modelProviderPaths, modelProvidersApi } from '@tests/support/api/model-providers-api'
import { actorSubject, as } from '@tests/support/auth/actors'
import { aProblem } from '@tests/support/builders/problem-details-builder'
import { aUser } from '@tests/support/builders/user-builder'
import { expect, test } from '@tests/support/fixtures'
import { modelProviders } from './model-providers.locators'

const signedInAs = (role: 'Admin' | 'User') => ({
  method: 'get' as const,
  path: membershipPaths.userOf(actorSubject),
  status: 200,
  body: aUser().identifiedBy(actorSubject).inRole(role).build(),
})

const noSessions = { method: 'get' as const, path: '/api/v1/sessions', status: 200, body: { items: [], nextCursor: null } }

test.describe('the model providers page', () => {
  test.use({ actor: as.user('Bob Smith') })

  test('an Admin connects a model provider, sends the key once, and no page shows the key again', async ({ page, api }) => {
    const provider = aProvider()
    api.mock(signedInAs('Admin'), noSessions, modelProvidersApi.listHolds([]), modelProvidersApi.addAnswers(provider))
    const at = modelProviders(page)
    await page.goto('/model-providers')
    await expect(at.heading).toBeVisible()

    await at.name.fill('Mine')
    await at.apiKey.fill('a-key-of-this-test')
    api.mock(modelProvidersApi.listHolds([provider]))
    await at.connect.click()

    await expect(at.providerHeading('Mine')).toBeVisible()
    await expect(at.name).toHaveValue('')
    await expect(at.apiKey).toHaveValue('')
    await expect(at.text('a-key-of-this-test')).toHaveCount(0)
    expect(api.last('post', modelProviderPaths.list)?.body).toMatchObject({ kind: 'Anthropic', name: 'Mine', apiKey: 'a-key-of-this-test' })
  })

  test('a refused connect shows the message on its field', async ({ page, api }) => {
    const message = 'Please provide the API key of the model provider.'
    api.mock(signedInAs('Admin'), noSessions, modelProvidersApi.listHolds([]), modelProvidersApi.addRefuses({ apiKey: [message] }))
    const at = modelProviders(page)
    await page.goto('/model-providers')

    await at.name.fill('Mine')
    await at.connect.click()

    await expect(at.fieldError(message)).toBeVisible()
  })

  test('the test button shows the models of the provider, and a pick saves the model', async ({ page, api }) => {
    const provider = aProvider()
    api.mock(
      signedInAs('Admin'),
      noSessions,
      modelProvidersApi.listHolds([provider]),
      modelProvidersApi.testAnswers(provider, { ok: true, models: [{ id: 'model-a', displayName: 'Model A' }] }),
      modelProvidersApi.updateAnswers({ ...provider, model: 'model-a' }),
    )
    const at = modelProviders(page)
    await page.goto('/model-providers')

    await at.test.click()
    await expect(at.connected).toContainText('offers 1 model.')
    await at.modelSelect('Mine').click()
    await at.modelOption('Model A').click()

    await expect.poll(() => api.last('put', modelProviderPaths.record(provider.id))?.body).toMatchObject({ model: 'model-a', version: 1 })
  })

  test('the test button shows the words of a provider that refuses', async ({ page, api }) => {
    const provider = aProvider()
    const words = 'Your API key is not valid.'
    api.mock(
      signedInAs('Admin'),
      noSessions,
      modelProvidersApi.listHolds([provider]),
      modelProvidersApi.testAnswers(provider, { ok: false, models: [], failure: { kind: 'Refused', statusCode: 401, message: words } }),
    )
    const at = modelProviders(page)
    await page.goto('/model-providers')

    await at.test.click()

    await expect(at.connectFailed).toContainText(words)
  })

  test('a provider with no model cannot be made active, and the page says why', async ({ page, api }) => {
    api.mock(signedInAs('Admin'), noSessions, modelProvidersApi.listHolds([aProvider({ isActive: false })]))
    const at = modelProviders(page)

    await page.goto('/model-providers')

    await expect(at.makeActive).toBeDisabled()
    await expect(at.text('Pick a model, then make it active.')).toBeVisible()
  })

  test('a provider with a model becomes active only when a person chooses it', async ({ page, api }) => {
    const provider = aProvider({ isActive: false, model: 'model-a' })
    api.mock(signedInAs('Admin'), noSessions, modelProvidersApi.listHolds([provider]), modelProvidersApi.updateAnswers({ ...provider, isActive: true }))
    const at = modelProviders(page)
    await page.goto('/model-providers')

    await at.makeActive.click()

    await expect.poll(() => api.last('put', modelProviderPaths.record(provider.id))?.body).toMatchObject({ makeActive: true, model: 'model-a' })
  })

  test('a new URL of a provider with a key needs the key again, and both go in one save', async ({ page, api }) => {
    const provider = aProvider({ kind: 'OpenAiCompatible', baseUrl: 'http://192.168.1.20:11434/v1', model: 'model-a' })
    api.mock(signedInAs('Admin'), noSessions, modelProvidersApi.listHolds([provider]), modelProvidersApi.updateAnswers(provider))
    const at = modelProviders(page)
    await page.goto('/model-providers')
    await expect(at.saveUrl).toBeDisabled()

    await at.newUrl.fill('http://192.168.1.21:11434/v1')
    await expect(at.saveUrl).toBeDisabled()
    await expect(at.text('A new URL needs the API key again.')).toBeVisible()
    await at.newApiKey.fill('a-key-of-this-test')
    await at.saveUrl.click()

    await expect
      .poll(() => api.last('put', modelProviderPaths.record(provider.id))?.body)
      .toMatchObject({ baseUrl: 'http://192.168.1.21:11434/v1', apiKey: 'a-key-of-this-test' })
  })

  test('a changed key ring shows a warning', async ({ page, api }) => {
    api.mock(signedInAs('Admin'), noSessions, modelProvidersApi.listHolds([aProvider({ keyState: 'Unreadable' })], 'Changed'))
    const at = modelProviders(page)

    await page.goto('/model-providers')

    await expect(at.keyRingWarning).toBeVisible()
    await expect(at.text('The server cannot read this key. Enter it again.')).toBeVisible()
  })

  test('a key folder that takes no write says that each saved key still works', async ({ page, api }) => {
    api.mock(signedInAs('Admin'), noSessions, modelProvidersApi.listHolds([aProvider()], 'NotWritable'))
    const at = modelProviders(page)

    await page.goto('/model-providers')

    await expect(at.text('Each saved API key still works')).toBeVisible()
    await expect(at.text('a new ring')).toHaveCount(0)
  })

  test('a member who is no Admin does not ask for the providers', async ({ page, api }) => {
    api.mock(signedInAs('User'), noSessions)
    const at = modelProviders(page)

    await page.goto('/model-providers')

    await expect(at.noAdminNote).toBeVisible()
    expect(api.last('get', modelProviderPaths.list)).toBeUndefined()
  })
})

test.describe('a page of model providers on a 390 px phone', () => {
  test.use({ actor: as.user('Bob Smith'), viewport: { width: 390, height: 800 } })

  test('a base URL stays whole on one line, and each control is 44 px high', async ({ page, api }) => {
    const provider = aProvider({ kind: 'OpenAiCompatible', baseUrl: 'http://host.docker.internal:26662/v1', model: 'model-a' })
    api.mock(signedInAs('Admin'), noSessions, modelProvidersApi.listHolds([provider]))
    const at = modelProviders(page)

    await page.goto('/model-providers')
    await expect(at.text('http://host.docker.internal:26662/v1')).toBeVisible()

    expect((await at.text('http://host.docker.internal:26662/v1').boundingBox())?.height).toBeLessThan(24)
    await expect(at.makeActive).toHaveCount(0)
    for (const control of await at.controls.all()) {
      expect((await control.boundingBox())?.height).toBeGreaterThanOrEqual(44)
    }
    expect(await at.pageOverflow()).toBe(0)
  })

  test('a long Topic and a long Person name take the width of the card on each phone', async ({ page, api }) => {
    const topic = 'a long topic that a person keeps for each of the things that a week holds'
    const person = 'a long name of a person that a model takes from each of the things in a week'
    const session = aSession({ cleanupStatus: 'Clean', cleanedText: 'Done.', peopleProposals: [{ label: person, isNew: true, contexts: [] }], suggestions: [{ kind: 'Topic', value: topic }] })
    api.mock(signedInAs('User'), modelProvidersApi.sessionIs(session), modelProvidersApi.activeIs('Home lab'))
    const at = modelProviders(page)

    await page.goto(`/model-providers?session=${session.id}`)
    await expect(at.text(topic)).toBeVisible()

    for (const width of [390, 430]) {
      await page.setViewportSize({ width, height: 800 })
      expect((await at.text(topic).boundingBox())?.height, `the Topic at ${width} px`).toBeLessThanOrEqual(60)
      expect((await at.text(person).boundingBox())?.height, `the Person at ${width} px`).toBeLessThanOrEqual(60)
      expect(await at.pageOverflow()).toBe(0)
    }
  })
})

test.describe('the Cleanup card on a 320 px phone', () => {
  test.use({ actor: as.user('Bob Smith'), viewport: { width: 320, height: 640 } })

  test.beforeEach(({ api }) => {
    api.mock(modelProvidersApi.activeIs('Home lab'))
  })

  test('a short suggestion stays on one line, with no sideways scroll', async ({ page, api }) => {
    const session = aSession({ cleanupStatus: 'Clean', cleanedText: 'Done.', suggestions: [{ kind: 'Mood', value: 'Calm' }] })
    api.mock(signedInAs('User'), modelProvidersApi.sessionIs(session))
    const at = modelProviders(page)

    await page.goto(`/model-providers?session=${session.id}`)

    await expect(at.text('Calm')).toBeVisible()
    expect((await at.text('Calm').boundingBox())?.height).toBeLessThan(24)
    expect(await at.pageOverflow()).toBe(0)
  })

  test('the two choices of a row stay on one line', async ({ page, api }) => {
    const session = aSession({
      cleanupStatus: 'Clean',
      cleanedText: 'Done.',
      suggestions: [{ kind: 'Mood', value: 'Calm' }],
      peopleProposals: [{ label: 'Sam', isNew: true, contexts: [] }],
    })
    api.mock(signedInAs('User'), modelProvidersApi.sessionIs(session))
    const at = modelProviders(page)

    await page.goto(`/model-providers?session=${session.id}`)
    await expect(at.rejectSuggestion('Calm')).toBeVisible()

    expect((await at.rejectSuggestion('Calm').boundingBox())?.y).toBe((await at.acceptSuggestion('Calm').boundingBox())?.y)
    expect((await at.doNotTagPerson('Sam').boundingBox())?.y).toBe((await at.tagPerson('Sam').boundingBox())?.y)
    expect(await at.pageOverflow()).toBe(0)
  })
})

test.describe('the Cleanup card', () => {
  test.use({ actor: as.user('Bob Smith') })

  const sessionUrl = (id: string) => `/model-providers?session=${id}`

  test.beforeEach(({ api }) => {
    api.mock(modelProvidersApi.activeIs('Home lab'))
  })

  test('a run shows that the person can leave, and a later visit finds Cleaned with Raw unchanged', async ({ page, api }) => {
    const session = aSession()
    const running = aSession({ cleanupStatus: 'Running' })
    api.mock(signedInAs('User'), modelProvidersApi.sessionIs(session), modelProvidersApi.cleanupStarts(running))
    const at = modelProviders(page)
    await page.goto(sessionUrl(session.id))
    await expect(at.runCleanup).toBeEnabled()

    api.mock(modelProvidersApi.sessionIs(running))
    await at.runCleanup.click()
    await expect(at.cleanupRunning).toBeVisible()
    await expect(at.runCleanup).toBeDisabled()
    await expect(at.runCleanup).toHaveText('Run Cleanup')

    await page.goto('/about')
    api.mock(modelProvidersApi.sessionIs(aSession({ cleanupStatus: 'Clean', cleanedText: 'I met Sam today.' })))
    await page.goto(sessionUrl(session.id))

    await expect(at.cleanedWords).toContainText('I met Sam today.')
    await expect(at.rawWords).toContainText('um I met sam today')
  })

  test('the card names the provider that gets the words, and it names none when no provider takes them', async ({ page, api }) => {
    const session = aSession()
    api.mock(signedInAs('User'), modelProvidersApi.sessionIs(session), modelProvidersApi.activeIs('Home lab'))
    const at = modelProviders(page)

    await page.goto(sessionUrl(session.id))
    await expect(at.wordsGoTo('Home lab')).toBeVisible()

    api.mock(modelProvidersApi.activeIs(null))
    await page.reload()
    await expect(at.noProviderNote).toBeVisible()
    await expect(at.runCleanup).toBeDisabled()
    await expect(at.wordsGoToAnyProvider).toHaveCount(0)
  })

  test('a person stops a running Cleanup, the page says so, and a new run is open', async ({ page, api }) => {
    const running = aSession({ cleanupStatus: 'Running' })
    const stopped = aSession({ cleanupStatus: 'Failed', cleanupFailure: 'Stopped' })
    api.mock(signedInAs('User'), modelProvidersApi.sessionIs(running), modelProvidersApi.cleanupStops(stopped))
    const at = modelProviders(page)
    await page.goto(sessionUrl(running.id))
    await expect(at.stopCleanup).toBeVisible()

    api.mock(modelProvidersApi.sessionIs(stopped))
    await at.stopCleanup.click()

    await expect(at.cleanupFailure('You stopped the Cleanup.')).toBeVisible()
    await expect(at.stopCleanup).toHaveCount(0)
    await expect(at.runCleanup).toBeEnabled()
    await expect(at.runCleanup).toHaveText('Run Cleanup again')
    await expect(at.rawWords).toContainText('um I met sam today')
    expect(api.last('post', modelProviderPaths.stopCleanup(running.id))).toBeDefined()
  })

  test('a finished run has no stop button', async ({ page, api }) => {
    const clean = aSession({ cleanupStatus: 'Clean', cleanedText: 'Done.' })
    api.mock(signedInAs('User'), modelProvidersApi.sessionIs(clean))
    const at = modelProviders(page)

    await page.goto(sessionUrl(clean.id))

    await expect(at.runCleanup).toHaveText('Run Cleanup again')
    await expect(at.stopCleanup).toHaveCount(0)
  })

  test('a Running run polls until the result arrives', async ({ page, api }) => {
    const running = aSession({ cleanupStatus: 'Running' })
    api.mock(signedInAs('User'), modelProvidersApi.sessionIs(running))
    const at = modelProviders(page)
    await page.goto(sessionUrl(running.id))
    await expect(at.cleanupRunning).toBeVisible()

    api.mock(modelProvidersApi.sessionIs(aSession({ cleanupStatus: 'Clean', cleanedText: 'Done now.' })))

    await expect(at.cleanedWords).toContainText('Done now.')
    await expect(at.cleanupRunning).toHaveCount(0)
  })

  test('a refused start shows why', async ({ page, api }) => {
    const session = aSession()
    const message = 'An Admin must connect a model provider and pick a model first.'
    api.mock(signedInAs('User'), modelProvidersApi.sessionIs(session), modelProvidersApi.cleanupRefuses(session, message))
    const at = modelProviders(page)
    await page.goto(sessionUrl(session.id))

    await at.runCleanup.click()

    await expect(at.cleanupRefused(message)).toBeVisible()
  })

  test('a failed run shows a closed reason', async ({ page, api }) => {
    const failed = aSession({ cleanupStatus: 'Failed', cleanupFailure: 'ProviderRefused' })
    api.mock(signedInAs('User'), modelProvidersApi.sessionIs(failed))
    const at = modelProviders(page)

    await page.goto(sessionUrl(failed.id))

    await expect(at.cleanupFailure('The model provider refused the request.')).toBeVisible()
  })

  test('a failed new run shows its reason while Raw is newer than Cleaned', async ({ page, api }) => {
    const failed = aSession({ cleanupStatus: 'Stale', cleanupFailure: 'ProviderRefused' })
    api.mock(signedInAs('User'), modelProvidersApi.sessionIs(failed))
    const at = modelProviders(page)

    await page.goto(sessionUrl(failed.id))

    await expect(at.cleanupFailure('The model provider refused the request.')).toBeVisible()
  })

  test('a tag leaves the choice of the Person to the server, so one name gives one Person', async ({ page, api }) => {
    const proposal = { label: 'Sam', matchedPersonId: null, isNew: true, contexts: ['I met Sam today.'] }
    const session = aSession({ cleanupStatus: 'Clean', cleanedText: 'I met Sam today.', peopleProposals: [proposal] })
    const respondPath = `/api/v1/sessions/${session.id}/people-proposals/respond`
    api.mock(signedInAs('User'), modelProvidersApi.sessionIs(session), {
      method: 'post',
      path: respondPath,
      status: 200,
      body: { ...session, peopleProposals: [] },
    })
    const at = modelProviders(page)
    await page.goto(sessionUrl(session.id))

    await at.tagPerson('Sam').click()

    await expect(at.tagPerson('Sam')).toHaveCount(0)
    expect(api.last('post', respondPath)?.body).toMatchObject({ label: 'Sam', approve: true, createNew: false })
  })

  test('a refused tag shows the words of the server', async ({ page, api }) => {
    const message = 'A tag of this name makes Cleaned too long. Choose Do not tag, or run a new Cleanup.'
    const proposal = { label: 'Sam', matchedPersonId: null, isNew: true, contexts: ['I met Sam today.'] }
    const session = aSession({ cleanupStatus: 'Clean', cleanedText: 'I met Sam today.', peopleProposals: [proposal] })
    api.mock(signedInAs('User'), modelProvidersApi.sessionIs(session), {
      method: 'post',
      path: `/api/v1/sessions/${session.id}/people-proposals/respond`,
      status: 422,
      body: aProblem().saying(message).invalid({ label: [message] }).build(),
    })
    const at = modelProviders(page)
    await page.goto(sessionUrl(session.id))

    await at.tagPerson('Sam').click()

    await expect(at.cleanupRefused(message)).toBeVisible()
    await expect(at.text(message)).toHaveCount(1)
  })

  test('hand edits warn before a new run', async ({ page, api }) => {
    const edited = aSession({ cleanupStatus: 'Clean', cleanedText: 'Mine.', cleanedHasHandEdits: true })
    api.mock(signedInAs('User'), modelProvidersApi.sessionIs(edited))
    const at = modelProviders(page)

    await page.goto(sessionUrl(edited.id))

    await expect(at.text('You edited Cleaned by hand.')).toBeVisible()
  })
})
