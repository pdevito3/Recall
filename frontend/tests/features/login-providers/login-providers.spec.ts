import { authApi } from '@tests/support/api/auth-api'
import { membershipApi } from '@tests/support/api/membership-api'
import {
  aLoginProvider,
  anEnvironmentProvider,
  callbackUrl,
  loginProviderPaths,
  loginProvidersApi,
} from '@tests/support/api/login-providers-api'
import { as } from '@tests/support/auth/actors'
import { expect, test } from '@tests/support/fixtures'
import { loginProvidersPage } from './login-providers.tasks'

const lastAdmin =
  'Recall refused this change, because the install needs an Admin who can sign in and manage access. Give another person who has signed in the role Admin and each Admin permission, then try again.'

test.describe('the login providers page', () => {
  test.use({ actor: as.user('Bob Smith') })

  test('lists a provider from the environment as read-only, then each row with its ID, issuer, and state', async ({ page, api }) => {
    api.mock(
      membershipApi.userHolds('manage_user_access'),
      loginProvidersApi.settingsHold(),
      loginProvidersApi.listHolds(
        [aLoginProvider(), aLoginProvider({ id: 'old-sso', displayName: 'Old SSO', issuer: 'https://sso.example.com', state: 'ReplacedByEnvironment' })],
        [anEnvironmentProvider()],
      ),
    )
    const lp = loginProvidersPage(page)

    await lp.open()

    await expect(lp.at.navLink).toBeVisible()
    await expect(lp.at.cardText('Company SSO', 'From the environment')).toBeVisible()
    await expect(lp.at.cardText('Company SSO', 'this page cannot change it')).toBeVisible()
    await expect(lp.at.edit('Company SSO')).toHaveCount(0)
    await expect(lp.at.delete('Company SSO')).toHaveCount(0)
    await expect(lp.at.cardText('Home IdP', 'home-idp')).toBeVisible()
    await expect(lp.at.cardText('Home IdP', 'https://id.example.com/realms/home')).toBeVisible()
    await expect(lp.at.cardText('Home IdP', 'On the sign-in page.')).toBeVisible()
  })

  test('a row that an entry of the environment replaces says so', async ({ page, api }) => {
    api.mock(
      membershipApi.userHolds('manage_user_access'),
      loginProvidersApi.settingsHold(),
      loginProvidersApi.listHolds([aLoginProvider({ id: 'old-sso', displayName: 'Old SSO', state: 'ReplacedByEnvironment' })], [anEnvironmentProvider()]),
    )
    const lp = loginProvidersPage(page)

    await lp.open()

    await expect(lp.at.cardText('Old SSO', 'Replaced by the environment')).toBeVisible()
    await expect(lp.at.cardText('Old SSO', 'the sign-in page shows that one in its place')).toBeVisible()
  })

  test('shows the callback URL to register, and copies it', { tag: '@IDP-8' }, async ({ page, context, api }) => {
    await context.grantPermissions(['clipboard-read', 'clipboard-write'])
    api.mock(membershipApi.userHolds('manage_user_access'), loginProvidersApi.settingsHold(), loginProvidersApi.listHolds([]))
    const lp = loginProvidersPage(page)
    await lp.open()

    await lp.at.copyCallbackUrl.click()

    await expect(lp.at.callbackUrl(callbackUrl)).toBeVisible()
    await expect.poll(() => page.evaluate(() => navigator.clipboard.readText())).toBe(callbackUrl)
  })

  test('adds a provider with each field of the form', async ({ page, api }) => {
    const added = aLoginProvider({ id: 'lan-idp', displayName: 'LAN IdP', issuer: 'http://192.168.1.5:8080', allowHttp: true })
    api.mock(membershipApi.userHolds('manage_user_access'), loginProvidersApi.settingsHold(), loginProvidersApi.listHolds([]), loginProvidersApi.addAnswers(added))
    const lp = loginProvidersPage(page)
    await lp.open()
    await lp.openAdd()

    await lp.fill({ id: 'lan-idp', displayName: 'LAN IdP', issuer: 'http://192.168.1.5:8080', clientId: 'recall', clientSecret: 'a-secret', caCertificatePath: '/data/ca.pem' })
    await lp.allowHttp()
    api.mock(loginProvidersApi.listHolds([added]))
    await lp.add()

    expect(api.last('post', loginProviderPaths.list)?.body).toEqual({
      id: 'lan-idp',
      displayName: 'LAN IdP',
      issuer: 'http://192.168.1.5:8080',
      clientId: 'recall',
      clientSecret: 'a-secret',
      allowHttp: true,
      caCertificatePath: '/data/ca.pem',
    })
    await expect(lp.at.dialog).toHaveCount(0)
    await expect(lp.at.card('LAN IdP')).toBeVisible()
  })

  test('a refused add shows the message on its field, keeps the input, and shows no toast', async ({ page, api }) => {
    const message = 'Give an issuer URL that starts with https://, or turn on Allow HTTP.'
    api.mock(membershipApi.userHolds('manage_user_access'), loginProvidersApi.settingsHold(), loginProvidersApi.listHolds([]), loginProvidersApi.addRefuses({ issuer: [message] }))
    const lp = loginProvidersPage(page)
    await lp.open()
    await lp.openAdd()

    await lp.fill({ id: 'lan-idp', displayName: 'LAN IdP', issuer: 'http://192.168.1.5:8080', clientId: 'recall', clientSecret: 'a-secret' })
    await lp.add()

    await expect(lp.at.dialogText(message)).toBeVisible()
    await expect(lp.at.issuerField).toBeFocused()
    await expect(lp.at.issuerField).toHaveValue('http://192.168.1.5:8080')
    expect(await lp.at.toast(message).count()).toBe(0)
  })

  test('an add with the ID of another provider shows the 409 on the ID', { tag: '@IDP-3' }, async ({ page, api }) => {
    const message = 'Another provider uses this ID. Pick another.'
    api.mock(membershipApi.userHolds('manage_user_access'), loginProvidersApi.settingsHold(), loginProvidersApi.listHolds([]), loginProvidersApi.addConflicts('id', message))
    const lp = loginProvidersPage(page)
    await lp.open()
    await lp.openAdd()

    await lp.fill({ id: 'home-idp', displayName: 'Home', issuer: 'https://id.example.com', clientId: 'recall', clientSecret: 'a-secret' })
    await lp.add()

    await expect(lp.at.dialogText(message)).toBeVisible()
    await expect(lp.at.idField).toBeFocused()
  })

  test('an add with the issuer of another provider shows the 409 on the issuer', { tag: '@IDP-3' }, async ({ page, api }) => {
    const message = 'Another provider uses this issuer.'
    api.mock(membershipApi.userHolds('manage_user_access'), loginProvidersApi.settingsHold(), loginProvidersApi.listHolds([]), loginProvidersApi.addConflicts('issuer', message))
    const lp = loginProvidersPage(page)
    await lp.open()
    await lp.openAdd()

    await lp.fill({ id: 'other-idp', displayName: 'Other', issuer: 'https://id.example.com', clientId: 'recall', clientSecret: 'a-secret' })
    await lp.add()

    await expect(lp.at.dialogText(message)).toBeVisible()
    await expect(lp.at.issuerField).toBeFocused()
  })

  test('a changed issuer clears the result of an earlier Test', { tag: '@IDP-7' }, async ({ page, api }) => {
    api.mock(
      membershipApi.userHolds('manage_user_access'),
      loginProvidersApi.settingsHold(),
      loginProvidersApi.listHolds([]),
      loginProvidersApi.testAnswers({ ok: true, issuer: 'https://id.example.com' }),
    )
    const lp = loginProvidersPage(page)
    await lp.open()
    await lp.openAdd()
    await lp.fill({ issuer: 'https://id.example.com' })
    await lp.test()
    await expect(lp.at.testPassed).toBeVisible()

    await lp.fill({ issuer: 'https://other.example.com' })

    await expect(lp.at.testPassed).toHaveCount(0)
  })

  test('Test sends the issuer, Allow HTTP, and the CA of the form, shows the issuer, and saves nothing', { tag: '@IDP-7' }, async ({ page, api }) => {
    api.mock(
      membershipApi.userHolds('manage_user_access'),
      loginProvidersApi.settingsHold(),
      loginProvidersApi.listHolds([]),
      loginProvidersApi.testAnswers({ ok: true, issuer: 'http://192.168.1.5:8080' }),
    )
    const lp = loginProvidersPage(page)
    await lp.open()
    await lp.openAdd()

    await lp.fill({ issuer: 'http://192.168.1.5:8080', caCertificatePath: '/data/ca.pem' })
    await lp.allowHttp()
    await lp.test()

    await expect(lp.at.testPassed).toContainText('Its issuer is http://192.168.1.5:8080.')
    expect(api.last('post', loginProviderPaths.test)?.body).toEqual({ issuer: 'http://192.168.1.5:8080', allowHttp: true, caCertificatePath: '/data/ca.pem' })
    expect(api.last('post', loginProviderPaths.list)).toBeUndefined()
  })

  test('Test shows the words of a failed discovery', { tag: '@IDP-7' }, async ({ page, api }) => {
    const words = 'The provider names another issuer: https://other.example.com.'
    api.mock(
      membershipApi.userHolds('manage_user_access'),
      loginProvidersApi.settingsHold(),
      loginProvidersApi.listHolds([]),
      loginProvidersApi.testAnswers({ ok: false, issuer: null, failure: { kind: 'OtherIssuer', message: words } }),
    )
    const lp = loginProvidersPage(page)
    await lp.open()
    await lp.openAdd()

    await lp.fill({ issuer: 'https://id.example.com' })
    await lp.test()

    await expect(lp.at.testFailed).toContainText(words)
  })

  test('a row whose secret the server cannot read says so and offers the edit', { tag: '@IDP-5' }, async ({ page, api }) => {
    api.mock(
      membershipApi.userHolds('manage_user_access'),
      loginProvidersApi.settingsHold(),
      loginProvidersApi.listHolds([aLoginProvider({ state: 'SecretUnreadable', secretState: 'Unreadable' })]),
    )
    const lp = loginProvidersPage(page)

    await lp.open()

    await expect(lp.at.cardText('Home IdP', 'The server cannot read this client secret. Enter it again.')).toBeVisible()
    await expect(lp.at.edit('Home IdP')).toBeVisible()
  })

  test('an edit keeps the issuer fixed, sends no issuer, and an empty secret keeps the stored one', async ({ page, api }) => {
    const provider = aLoginProvider()
    api.mock(membershipApi.userHolds('manage_user_access'), loginProvidersApi.settingsHold(), loginProvidersApi.listHolds([provider]), loginProvidersApi.updateAnswers(provider))
    const lp = loginProvidersPage(page)
    await lp.open()
    await lp.openEdit('Home IdP')

    await lp.fill({ displayName: 'Home' })
    await lp.save(provider.id)

    expect(api.last('put', loginProviderPaths.record(provider.id))?.body).toEqual({
      displayName: 'Home',
      clientId: 'recall',
      clientSecret: null,
      allowHttp: false,
      caCertificatePath: null,
      version: 1,
    })
  })

  test('the edit form shows the issuer as read-only, has no ID field, and asks password managers not to fill the secret', async ({ page, api }) => {
    api.mock(membershipApi.userHolds('manage_user_access'), loginProvidersApi.settingsHold(), loginProvidersApi.listHolds([aLoginProvider()]))
    const lp = loginProvidersPage(page)
    await lp.open()

    await lp.openEdit('Home IdP')

    await expect(lp.at.issuerField).toHaveValue('https://id.example.com/realms/home')
    await expect(lp.at.issuerField).toHaveAttribute('readonly', '')
    await expect(lp.at.idField).toHaveCount(0)
    await expect(lp.at.clientSecretField).toHaveAttribute('autocomplete', 'new-password')
    await expect(lp.at.clientSecretField).toHaveAttribute('data-1p-ignore', 'true')
  })

  test('the edit of a row whose secret the server cannot read asks for it, and sends the new secret', { tag: '@IDP-5' }, async ({ page, api }) => {
    const provider = aLoginProvider({ state: 'SecretUnreadable', secretState: 'Unreadable' })
    api.mock(membershipApi.userHolds('manage_user_access'), loginProvidersApi.settingsHold(), loginProvidersApi.listHolds([provider]), loginProvidersApi.updateAnswers(aLoginProvider()))
    const lp = loginProvidersPage(page)
    await lp.open()
    await lp.openEdit('Home IdP')
    await expect(lp.at.dialogText('The server cannot read the saved secret. Enter it again.')).toBeVisible()

    await lp.fill({ clientSecret: 'the-new-secret' })
    await lp.save(provider.id)

    expect(api.last('put', loginProviderPaths.record(provider.id))?.body).toMatchObject({ clientSecret: 'the-new-secret' })
  })

  test('a stale save keeps the edits and offers to load the latest', async ({ page, api }) => {
    const provider = aLoginProvider()
    api.mock(membershipApi.userHolds('manage_user_access'), loginProvidersApi.settingsHold(), loginProvidersApi.listHolds([provider]), loginProvidersApi.updateIsStale(provider))
    const lp = loginProvidersPage(page)
    await lp.open()
    await lp.openEdit('Home IdP')

    await lp.fill({ displayName: 'Home' })
    await lp.save(provider.id)

    await expect(lp.at.staleAlert).toBeVisible()
    await expect(lp.at.nameField).toHaveValue('Home')
  })

  test('Load the latest takes the field that the other save changed, and keeps the typed edit and secret', async ({ page, api }) => {
    const provider = aLoginProvider()
    api.mock(membershipApi.userHolds('manage_user_access'), loginProvidersApi.settingsHold(), loginProvidersApi.listHolds([provider]), loginProvidersApi.updateIsStale(provider))
    const lp = loginProvidersPage(page)
    await lp.open()
    await lp.openEdit('Home IdP')
    await lp.fill({ displayName: 'Home', clientSecret: 'the-new-secret' })
    await lp.save(provider.id)

    api.mock(loginProvidersApi.listHolds([{ ...provider, clientId: 'recall-2', version: 2 }]))
    await lp.loadLatest()

    await expect(lp.at.clientIdField).toHaveValue('recall-2')
    await expect(lp.at.nameField).toHaveValue('Home')
    await expect(lp.at.clientSecretField).toHaveValue('the-new-secret')
  })

  test('a failed Load the latest says so and keeps the edits', async ({ page, api }) => {
    const provider = aLoginProvider()
    api.mock(membershipApi.userHolds('manage_user_access'), loginProvidersApi.settingsHold(), loginProvidersApi.listHolds([provider]), loginProvidersApi.updateIsStale(provider))
    const lp = loginProvidersPage(page)
    await lp.open()
    await lp.openEdit('Home IdP')
    await lp.fill({ displayName: 'Home' })
    await lp.save(provider.id)

    api.mock(loginProvidersApi.listFails())
    await lp.loadLatest()

    await expect(lp.at.latestFailed).toBeVisible()
    await expect(lp.at.nameField).toHaveValue('Home')
  })

  test('a delete after the confirm removes the row', async ({ page, api }) => {
    const provider = aLoginProvider()
    api.mock(membershipApi.userHolds('manage_user_access'), loginProvidersApi.settingsHold(), loginProvidersApi.listHolds([provider]), loginProvidersApi.deleteWorks(provider))
    const lp = loginProvidersPage(page)
    await lp.open()

    api.mock(loginProvidersApi.listHolds([]))
    await lp.delete('Home IdP', provider.id)

    await expect(lp.at.card('Home IdP')).toHaveCount(0)
    await expect(lp.at.listHeading).toBeFocused()
  })

  test('a refused delete shows the detail of the last-Admin rule on the row', async ({ page, api }) => {
    const provider = aLoginProvider()
    api.mock(membershipApi.userHolds('manage_user_access'), loginProvidersApi.settingsHold(), loginProvidersApi.listHolds([provider]), loginProvidersApi.deleteConflicts(provider, lastAdmin))
    const lp = loginProvidersPage(page)
    await lp.open()

    await lp.delete('Home IdP', provider.id)

    await expect(lp.at.cardAlert('Home IdP')).toHaveText(lastAdmin)
    expect(await lp.at.toast(lastAdmin).count()).toBe(0)
  })

  test('a person with no manage_user_access sees no link and does not ask for the providers', async ({ page, api }) => {
    api.mock(membershipApi.userHolds('manage_model_providers'))
    const lp = loginProvidersPage(page)

    await page.goto('/login-providers')

    await expect(lp.at.noAdminNote).toBeVisible()
    await expect(lp.at.navLink).toHaveCount(0)
    expect(api.last('get', loginProviderPaths.list)).toBeUndefined()
  })
})

const noLinkedAdmin = 'Each tenant needs an Admin who can sign in with a provider before password login turns off.'

test.describe('password login on the login providers page', () => {
  test.use({ actor: as.user('Bob Smith') })

  test('shows On, and a turn off asks first and says what changes', { tag: '@IDP-13' }, async ({ page, api }) => {
    api.mock(membershipApi.userHolds('manage_user_access'), loginProvidersApi.settingsHold('On', 4), loginProvidersApi.listHolds([aLoginProvider()]))
    const lp = loginProvidersPage(page)
    await lp.open()
    await expect(lp.at.passwordLoginState('On')).toBeVisible()

    await lp.askToTurnPasswordsOff()

    await expect(lp.at.turnOffAskText(/No one signs in with a password/)).toBeVisible()
    await expect(lp.at.turnOffAskText(/only links a provider/)).toBeVisible()
    await expect(lp.at.turnOffAskText(/claim-owner file of the server still works/)).toBeVisible()
    expect(api.last('put', loginProviderPaths.settings)).toBeUndefined()
  })

  test('a confirmed turn off sends the version that it read and shows Off', { tag: ['@IDP-13', '@IDP-15'] }, async ({ page, api }) => {
    api.mock(
      membershipApi.userHolds('manage_user_access'),
      loginProvidersApi.settingsHold('On', 4),
      loginProvidersApi.listHolds([aLoginProvider()]),
      loginProvidersApi.settingsUpdateAnswers('Off', 5),
    )
    const lp = loginProvidersPage(page)
    await lp.open()
    await lp.askToTurnPasswordsOff()

    await lp.turnPasswordsOff()

    expect(api.last('put', loginProviderPaths.settings)?.body).toEqual({ passwordLogin: 'Off', version: 4 })
    await expect(lp.at.passwordLoginState('Off')).toBeVisible()
    await expect(lp.at.passwordLoginText(/A temporary password only links a provider/)).toBeVisible()
    await expect(lp.at.turnOn).toBeVisible()
    await expect(lp.at.passwordLoginHeading).toBeFocused()
  })

  test('a turn on needs no confirm', { tag: '@IDP-13' }, async ({ page, api }) => {
    api.mock(
      membershipApi.userHolds('manage_user_access'),
      loginProvidersApi.settingsHold('Off', 5),
      loginProvidersApi.listHolds([aLoginProvider()]),
      loginProvidersApi.settingsUpdateAnswers('On', 6),
    )
    const lp = loginProvidersPage(page)
    await lp.open()

    await lp.turnPasswordsOn()

    expect(api.last('put', loginProviderPaths.settings)?.body).toEqual({ passwordLogin: 'On', version: 5 })
    await expect(lp.at.passwordLoginState('On')).toBeVisible()
    expect(await lp.at.turnOffAsk.count()).toBe(0)
  })

  test('a refused turn off shows the message of the Server on the card, and no toast', { tag: '@IDP-13' }, async ({ page, api }) => {
    api.mock(
      membershipApi.userHolds('manage_user_access'),
      loginProvidersApi.settingsHold('On', 4),
      loginProvidersApi.listHolds([]),
      loginProvidersApi.settingsUpdateConflicts(noLinkedAdmin),
    )
    const lp = loginProvidersPage(page)
    await lp.open()
    await lp.askToTurnPasswordsOff()

    await lp.turnPasswordsOff()

    await expect(lp.at.passwordLoginAlert).toHaveText(noLinkedAdmin)
    await expect(lp.at.passwordLoginState('On')).toBeVisible()
    expect(await lp.at.toast(noLinkedAdmin).count()).toBe(0)
  })

  test('a stale turn shows the message, the setting that another Admin saved, and settings with no change of a password', { tag: ['@IDP-13', '@IDP-15'] }, async ({ page, api, actor }) => {
    const withPassword = actor.signedIn ? { ...actor, user: { ...actor.user, login_name: 'bob' } } : actor
    api.mock(
      authApi.userIs(withPassword),
      membershipApi.userHolds('manage_user_access'),
      loginProvidersApi.settingsHold('On', 4),
      loginProvidersApi.listHolds([aLoginProvider()]),
      loginProvidersApi.settingsUpdateIsStale(),
    )
    const lp = loginProvidersPage(page)
    await lp.open()
    await lp.askToTurnPasswordsOff()

    // Another Admin turned it off, so /auth/me now gives no login name.
    api.mock(loginProvidersApi.settingsHold('Off', 5), authApi.userIs(actor))
    await lp.turnPasswordsOff()

    await expect(lp.at.passwordLoginAlert).toHaveText('Password login changed after you loaded this page. The card shows the setting now.')
    await expect(lp.at.passwordLoginState('Off')).toBeVisible()
    await expect(lp.at.turnOn).toBeVisible()
    await lp.openSettings()
    await expect(lp.at.passwordHeading).toHaveCount(0)
  })

  test('after a turn off, settings reads the user again and offers no change of a password', { tag: '@IDP-15' }, async ({ page, api, actor }) => {
    const withPassword = actor.signedIn ? { ...actor, user: { ...actor.user, login_name: 'bob' } } : actor
    api.mock(
      authApi.userIs(withPassword),
      membershipApi.userHolds('manage_user_access'),
      loginProvidersApi.settingsHold('On', 4),
      loginProvidersApi.listHolds([aLoginProvider()]),
      loginProvidersApi.settingsUpdateAnswers('Off', 5),
    )
    const lp = loginProvidersPage(page)
    await lp.open()
    await lp.askToTurnPasswordsOff()

    // While password login is Off, /auth/me gives no login name, and the page with no reload keeps the user that it read.
    api.mock(authApi.userIs(actor))
    await lp.turnPasswordsOff()
    await lp.openSettings()

    await expect(lp.at.passwordHeading).toHaveCount(0)
  })

  test('with no provider and password login off, the list says that only the claim-owner file lets anyone in', { tag: '@IDP-15' }, async ({ page, api }) => {
    api.mock(membershipApi.userHolds('manage_user_access'), loginProvidersApi.settingsHold('Off', 5), loginProvidersApi.listHolds([]))
    const lp = loginProvidersPage(page)

    await lp.open()

    await expect(lp.at.passwordLoginText(/claim-owner file/)).toBeVisible()
    await expect(lp.at.onlyClaimOwnerNote).toBeVisible()
  })
})

test.describe('the login providers page on a 390 px phone', () => {
  test.use({ actor: as.user('Bob Smith'), viewport: { width: 390, height: 800 } })

  test('nothing runs past the edge, and each control is 44 px high', async ({ page, api }) => {
    const longName = `Maximilian${'bartholomew'.repeat(8)}`
    api.mock(
      membershipApi.userHolds('manage_user_access'),
      loginProvidersApi.settingsHold(),
      loginProvidersApi.listHolds([aLoginProvider({ displayName: longName, issuer: `https://${'idp'.repeat(30)}.example.com/realms/home` })], [anEnvironmentProvider()]),
    )
    const lp = loginProvidersPage(page)

    await lp.open()

    expect(await lp.at.pageOverflow()).toBe(0)
    for (const control of await lp.at.controls.all()) {
      expect((await control.boundingBox())?.height).toBeGreaterThanOrEqual(44)
    }
  })
})
