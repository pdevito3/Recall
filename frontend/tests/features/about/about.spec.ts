import { membershipPaths } from '@tests/support/api/membership-api'
import { supportApi, supportPaths } from '@tests/support/api/support-api'
import { actorSubject, as } from '@tests/support/auth/actors'
import { aUser } from '@tests/support/builders/user-builder'
import { expect, test } from '@tests/support/fixtures'
import { appShellPage } from '../app-shell/app-shell.tasks'
import { homePage } from '../home/home.tasks'
import { aboutPage } from './about.tasks'

const signedInAs = (role: 'Admin' | 'User') => ({
  method: 'get' as const,
  path: membershipPaths.userOf(actorSubject),
  status: 200,
  body: aUser().identifiedBy(actorSubject).inRole(role).build(),
})

test.describe('the support bundle on the about page', () => {
  test.use({ actor: as.user('Bob Smith') })

  test('an Admin downloads one zip with the CSRF header on the request', async ({ page, api }) => {
    api.mock(signedInAs('Admin'), supportApi.bundleIsReady())
    const about = aboutPage(page)
    await about.open()

    const download = await about.downloadSupportBundle()

    expect(download.suggestedFilename()).toBe('recall-support.zip')
    expect(api.last('get', supportPaths.bundle)?.headers.get('x-csrf')).toBe('1')
  })

  test('an Admin who is not the Owner reads the refusal of the Server', async ({ page, api }) => {
    const refusal = 'Only the Owner of this install can download the support bundle.'
    api.mock(signedInAs('Admin'), supportApi.bundleRefuses(refusal))
    const about = aboutPage(page)
    await about.open()

    await about.at.supportBundleButton.click()

    await expect(about.at.refusal(refusal)).toBeVisible()
  })

  test('a Member does not see the section', async ({ page, api }) => {
    api.mock(signedInAs('User'))
    const about = aboutPage(page)
    const membershipLoaded = page.waitForResponse((response) => response.url().endsWith(membershipPaths.userOf(actorSubject)))
    await homePage(page).open()
    await membershipLoaded

    // The route changes with no reload, so the page renders with the answer that the home page already has.
    await appShellPage(page).at.aboutNavigationLink.click()
    await expect(about.at.text).toBeVisible()

    expect(await about.at.supportHeading.count()).toBe(0)
  })
})
