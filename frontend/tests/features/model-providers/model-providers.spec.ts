import { membershipPaths } from '@tests/support/api/membership-api'
import { aProvider, modelProviderPaths, modelProvidersApi } from '@tests/support/api/model-providers-api'
import { sessionsPaths } from '@tests/support/api/sessions-api'
import { actorSubject, as } from '@tests/support/auth/actors'
import { aUser } from '@tests/support/builders/user-builder'
import { expect, test } from '@tests/support/fixtures'
import { modelProviders } from './model-providers.locators'

const signedInAs = (role: 'Admin' | 'User') => ({
  method: 'get' as const,
  path: membershipPaths.userOf(actorSubject),
  status: 200,
  body: aUser().identifiedBy(actorSubject).inRole(role).build(),
})

test.describe('the model providers page', () => {
  test.use({ actor: as.user('Bob Smith') })

  test('an Admin connects a model provider, sends the key once, and no page shows the key again', async ({ page, api }) => {
    const provider = aProvider()
    api.mock(signedInAs('Admin'), modelProvidersApi.listHolds([]), modelProvidersApi.addAnswers(provider))
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
    api.mock(signedInAs('Admin'), modelProvidersApi.listHolds([]), modelProvidersApi.addRefuses({ apiKey: [message] }))
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
      modelProvidersApi.listHolds([provider]),
      modelProvidersApi.testAnswers(provider, { ok: false, models: [], failure: { kind: 'Refused', statusCode: 401, message: words } }),
    )
    const at = modelProviders(page)
    await page.goto('/model-providers')

    await at.test.click()

    await expect(at.connectFailed).toContainText(words)
  })

  test('a provider with no model cannot be made active, and the page says why', async ({ page, api }) => {
    api.mock(signedInAs('Admin'), modelProvidersApi.listHolds([aProvider({ isActive: false })]))
    const at = modelProviders(page)

    await page.goto('/model-providers')

    await expect(at.makeActive).toBeDisabled()
    await expect(at.text('Pick a model, then make it active.')).toBeVisible()
  })

  test('a provider with a model becomes active only when a person chooses it', async ({ page, api }) => {
    const provider = aProvider({ isActive: false, model: 'model-a' })
    api.mock(signedInAs('Admin'), modelProvidersApi.listHolds([provider]), modelProvidersApi.updateAnswers({ ...provider, isActive: true }))
    const at = modelProviders(page)
    await page.goto('/model-providers')

    await at.makeActive.click()

    await expect.poll(() => api.last('put', modelProviderPaths.record(provider.id))?.body).toMatchObject({ makeActive: true, model: 'model-a' })
  })

  test('after Make active, the focus goes to the name of the provider, because the button goes away', async ({ page, api }) => {
    const provider = aProvider({ isActive: false, model: 'model-a' })
    api.mock(signedInAs('Admin'), modelProvidersApi.listHolds([provider]), modelProvidersApi.updateAnswers({ ...provider, isActive: true }))
    const at = modelProviders(page)
    await page.goto('/model-providers')
    await expect(at.makeActive).toBeEnabled()

    api.mock(modelProvidersApi.listHolds([{ ...provider, isActive: true }]))
    await at.makeActive.click()

    await expect(at.makeActive).toHaveCount(0)
    await expect(at.providerHeading('Mine')).toBeFocused()
  })

  test('a new URL of a provider with a key needs the key again, and both go in one save', async ({ page, api }) => {
    const provider = aProvider({ kind: 'OpenAiCompatible', baseUrl: 'http://192.168.1.20:11434/v1', model: 'model-a' })
    api.mock(signedInAs('Admin'), modelProvidersApi.listHolds([provider]), modelProvidersApi.updateAnswers(provider))
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
    api.mock(signedInAs('Admin'), modelProvidersApi.listHolds([aProvider({ keyState: 'Unreadable' })], 'Changed'))
    const at = modelProviders(page)

    await page.goto('/model-providers')

    await expect(at.keyRingWarning).toBeVisible()
    await expect(at.text('The server cannot read this key. Enter it again.')).toBeVisible()
  })

  test('a key folder that takes no write says that each saved key still works', async ({ page, api }) => {
    api.mock(signedInAs('Admin'), modelProvidersApi.listHolds([aProvider()], 'NotWritable'))
    const at = modelProviders(page)

    await page.goto('/model-providers')

    await expect(at.text('Each saved API key still works')).toBeVisible()
    await expect(at.text('a new ring')).toHaveCount(0)
  })

  test('a member who is no Admin does not ask for the providers', async ({ page, api }) => {
    api.mock(signedInAs('User'))
    const at = modelProviders(page)

    await page.goto('/model-providers')

    await expect(at.noAdminNote).toBeVisible()
    expect(api.last('get', modelProviderPaths.list)).toBeUndefined()
  })

  test('the page runs no Cleanup and reads no entry, because a Cleanup runs on the page of its entry', async ({ page, api }) => {
    api.mock(signedInAs('Admin'), modelProvidersApi.listHolds([aProvider()]))
    const at = modelProviders(page)

    await page.goto('/model-providers?session=ses_1')
    await expect(at.providerHeading('Mine')).toBeVisible()

    await expect(at.anyCleanup).toHaveCount(0)
    expect(api.last('get', sessionsPaths.timeline)).toBeUndefined()
    expect(api.last('get', sessionsPaths.one('ses_1'))).toBeUndefined()
  })
})

test.describe('the frame of the model providers page', () => {
  test.use({ actor: as.user('Bob Smith') })

  for (const width of [1280, 390]) {
    test(`its heading starts where the heading of each page starts, at ${width} px`, async ({ page, api }) => {
      await page.setViewportSize({ width, height: 800 })
      api.mock(signedInAs('Admin'), modelProvidersApi.listHolds([]))
      const at = modelProviders(page)

      await page.goto('/about')
      await expect(at.aboutHeading).toBeVisible()
      const aboutX = (await at.aboutHeading.boundingBox())?.x
      await page.goto('/model-providers')
      await expect(at.heading).toBeVisible()

      expect((await at.heading.boundingBox())?.x).toBe(aboutX)
      expect(await at.pageOverflow()).toBe(0)
    })
  }
})

test.describe('a page of model providers on a 390 px phone', () => {
  test.use({ actor: as.user('Bob Smith'), viewport: { width: 390, height: 800 } })

  test('a base URL stays whole on one line, and each control is 44 px high', async ({ page, api }) => {
    const provider = aProvider({ kind: 'OpenAiCompatible', baseUrl: 'http://host.docker.internal:26662/v1', model: 'model-a' })
    api.mock(signedInAs('Admin'), modelProvidersApi.listHolds([provider]))
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

  test('a base URL longer than a line breaks after a slash, not one character short of its end', async ({ page, api }) => {
    const address = 'http://host.docker.internal:31742/m/gate_mid/openai/v1'
    const provider = aProvider({ kind: 'OpenAiCompatible', baseUrl: address, model: 'model-a' })
    api.mock(signedInAs('Admin'), modelProvidersApi.listHolds([provider]))
    const at = modelProviders(page)

    await page.goto('/model-providers')
    await expect(at.text(address)).toBeVisible()

    expect((await at.text(address).boundingBox())?.height).toBeGreaterThan(24)
    expect(await at.addressPartsThatBreak(address)).toEqual([])
  })

  test('a long name of a provider breaks in the label of its model, so no text runs out of the card', async ({ page, api }) => {
    const name = `Maximilian${'bartholomew'.repeat(8)}`
    const provider = aProvider({ name, model: 'model-a' })
    api.mock(
      signedInAs('Admin'),
      modelProvidersApi.listHolds([provider]),
      modelProvidersApi.testAnswers(provider, { ok: true, models: [{ id: 'model-a', displayName: 'Model A' }] }),
    )
    const at = modelProviders(page)
    await page.goto('/model-providers')

    await at.test.click()
    await expect(at.modelLabel(name)).toBeVisible()

    expect(await at.modelLabel(name).evaluate((label) => label.scrollWidth - label.clientWidth)).toBe(0)
    expect(await at.pageOverflow()).toBe(0)
  })

  test('Save URL and Save key each sit under their input', async ({ page, api }) => {
    const provider = aProvider({ kind: 'OpenAiCompatible', baseUrl: 'http://host.docker.internal:26662/v1', model: 'model-a' })
    api.mock(signedInAs('Admin'), modelProvidersApi.listHolds([provider]))
    const at = modelProviders(page)

    await page.goto('/model-providers')
    await expect(at.saveKey).toBeVisible()

    const below = async (input: typeof at.newUrl, button: typeof at.saveUrl) => {
      const field = (await input.boundingBox())!
      const action = (await button.boundingBox())!
      return { under: action.y >= field.y + field.height, sameLeft: action.x === field.x }
    }
    expect(await below(at.newUrl, at.saveUrl)).toEqual({ under: true, sameLeft: true })
    expect(await below(at.newApiKey, at.saveKey)).toEqual({ under: true, sameLeft: true })
  })
})
