import { aLink, externalLoginsApi, externalLoginsPaths } from '@tests/support/api/external-logins-api'
import { aProblem } from '@tests/support/builders/problem-details-builder'
import { expect, test } from '@tests/support/fixtures'
import { settingsPage } from './settings.tasks'

const providerUrl = 'https://idp.example/authorize?state=s'

test.describe('the sign-in methods of a person', () => {
  test('lists each link with its provider and label, and offers each provider that is not linked', { tag: '@IDP-11' }, async ({ page, api }) => {
    api.mock(
      externalLoginsApi.mineAre([aLink()]),
      externalLoginsApi.providersAre([{ id: 'work', displayName: 'Work SSO' }, { id: 'home', displayName: 'Home ID' }]),
    )
    const settings = settingsPage(page)

    await settings.open()

    const link = settings.at.signInMethod('Work SSO, bob@work.example')
    await expect(link).toBeVisible()
    await expect(link).toContainText('Last sign-in on')
    await expect(settings.at.linkButton('Home ID')).toBeVisible()
    await expect(settings.at.linkButton('Work SSO')).toHaveCount(0)
  })

  test('a link goes to the provider that the Server names', { tag: '@IDP-9' }, async ({ page, api }) => {
    api.mock(externalLoginsApi.providersAre([{ id: 'home', displayName: 'Home ID' }]), externalLoginsApi.linkStarts('home', providerUrl))
    const settings = settingsPage(page)
    await settings.providerAnswersAt('https://idp.example')
    await settings.open()

    await settings.at.linkButton('Home ID').click()

    await settings.arrivesAt(providerUrl)
    expect(api.last('post', externalLoginsPaths.link('home'))?.headers.get('x-csrf')).toBe('1')
  })

  test('an old session sees the message of the Server and a way to sign in again', { tag: '@IDP-10' }, async ({ page, api }) => {
    const message = 'Sign in again, then link the provider within 15 minutes.'
    api.mock(
      externalLoginsApi.providersAre([{ id: 'home', displayName: 'Home ID' }]),
      externalLoginsApi.linkRefused('home', aProblem().signInAgainToLink().build()),
    )
    const settings = settingsPage(page)
    await settings.open()

    await settings.at.linkButton('Home ID').click()

    await expect(settings.at.alertSaying(message)).toBeVisible()
    await expect(settings.at.signInAgain).toBeVisible()
  })

  test('another refusal of a link shows its message and offers no sign-out', async ({ page, api }) => {
    const message = 'Link a provider from Recall in a web browser.'
    api.mock(
      externalLoginsApi.providersAre([{ id: 'home', displayName: 'Home ID' }]),
      externalLoginsApi.linkRefused('home', aProblem().forbidden(message).build()),
    )
    const settings = settingsPage(page)
    await settings.open()

    await settings.at.linkButton('Home ID').click()

    await expect(settings.at.alertSaying(message)).toBeVisible()
    await expect(settings.at.signInAgain).toHaveCount(0)
  })

  for (const [result, words] of [
    ['linked', 'Recall linked the provider. You can sign in with it now.'],
    ['conflict', 'This account at the provider is linked to another person in Recall.'],
    ['refused', 'Recall did not link the provider, because the link started in another browser or session. Start it again from settings.'],
  ] as const) {
    test(`the return of a link shows its result: ${result}`, { tag: result === 'refused' ? '@IDP-9' : '@IDP-11' }, async ({ page }) => {
      const settings = settingsPage(page)

      await page.goto(`/settings?link=${result}`)

      await expect(settings.at.message(words)).toBeVisible()
    })
  }

  test('the result of a link leaves the URL, so a reload does not show it again', { tag: '@IDP-11' }, async ({ page }) => {
    const settings = settingsPage(page)
    const words = 'Recall linked the provider. You can sign in with it now.'
    await page.goto('/settings?link=linked')
    await expect(settings.at.message(words)).toBeVisible()

    await expect(page).toHaveURL(/\/settings$/)
    await page.reload()

    await expect(settings.at.heading).toBeVisible()
    await expect(settings.at.message(words)).toHaveCount(0)
  })

  test('removes a link, and sees the refusal of the last Admin at the link', { tag: '@IDP-12' }, async ({ page, api }) => {
    const refused = 'Recall refused this change, because the install needs an Admin who can sign in and manage access. Give another person who has signed in the role Admin and each Admin permission, then try again.'
    api.mock(externalLoginsApi.mineAre([aLink()]), externalLoginsApi.removeOfMineFails(aProblem().conflict(refused).build()))
    const settings = settingsPage(page)
    await settings.open()

    await settings.removeSignInMethod('Work SSO, bob@work.example')

    await expect(settings.at.signInMethodAlert('Work SSO, bob@work.example')).toHaveText(refused)
    expect(api.last('delete', externalLoginsPaths.mine)?.body).toEqual({ issuer: 'https://idp.example', subject: 'sub-1' })
  })

  test('the section stays in the page on a phone', async ({ page, api }) => {
    await page.setViewportSize({ width: 390, height: 844 })
    api.mock(
      externalLoginsApi.mineAre([aLink({ displayLabel: `${'a'.repeat(120)}@example.com` })]),
      externalLoginsApi.providersAre([{ id: 'home', displayName: 'Home ID' }]),
    )
    const settings = settingsPage(page)

    await settings.open()

    await expect(settings.at.linkButton('Home ID')).toBeVisible()
    expect(await settings.sideScrollRange()).toBe(0)
  })
})
