import { membershipApi } from '@tests/support/api/membership-api'
import { as } from '@tests/support/auth/actors'
import { expect, test } from '@tests/support/fixtures'
import { appShellPage } from '../app-shell/app-shell.tasks'
import { membership } from './membership.locators'

test.describe('the membership check', () => {
  test.use({ actor: as.user('Bob Smith') })

  test('a refusal of the Server shows its message and no way to try again', async ({ page, api }) => {
    const refusal = 'You no longer have access to this tenant.'
    api.mock(membershipApi.lookupRefuses(refusal))
    const at = membership(page)

    await appShellPage(page).openAbout()

    await expect(at.notice).toContainText(refusal)
    expect(await at.tryAgain.count()).toBe(0)
  })

  test('a user with a record sees no notice', async ({ page }) => {
    const at = membership(page)

    await appShellPage(page).openAbout()

    await expect(at.notice).toHaveCount(0)
  })
})
