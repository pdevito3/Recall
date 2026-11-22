import { aLink, externalLoginsApi, externalLoginsPaths } from '@tests/support/api/external-logins-api'
import { personalAccessTokensApi } from '@tests/support/api/personal-access-tokens-api'
import { usersApi } from '@tests/support/api/users-api'
import { as } from '@tests/support/auth/actors'
import { aUser } from '@tests/support/builders/user-builder'
import { expect, test } from '@tests/support/fixtures'
import { usersPage } from './users.tasks'

test.describe('the sign-in methods on the page of a user', () => {
  test.use({ actor: as.user('Ana Admin') })

  test.beforeEach(({ api }) => api.mock(personalAccessTokensApi.noneForAnyUser()))

  test('an Admin who manages access sees and removes the links of the user', { tag: '@IDP-12' }, async ({ page, api }) => {
    const grace = aUser().named('Grace Hopper').allowing('Update', 'ManageExternalLogins')
    const id = grace.build().id
    api.mock(usersApi.oneIs(grace), externalLoginsApi.ofUserAre(id, [aLink({ displayLabel: 'grace@navy.example' })]), externalLoginsApi.removesOfUser(id))
    const users = usersPage(page)
    await users.openDetail(id)

    await expect(users.at.signInMethod('Grace Hopper', 'Work SSO, grace@navy.example')).toBeVisible()
    await users.removeSignInMethod('Work SSO, grace@navy.example')

    await expect(users.at.signInMethodRemoved).toBeVisible()
    expect(api.last('delete', externalLoginsPaths.ofUser(id))?.body).toEqual({ issuer: 'https://idp.example', subject: 'sub-1' })
  })

  test('a caller who cannot manage the links of the user sees no section and asks for none', { tag: '@IDP-12' }, async ({ page, api }) => {
    const grace = aUser().named('Grace Hopper').allowing('Update')
    const id = grace.build().id
    api.mock(usersApi.oneIs(grace))
    const users = usersPage(page)
    await users.openDetail(id)

    await expect(users.at.detailHeading('Grace Hopper')).toBeVisible()
    await expect(users.at.signInMethodsHeading).toHaveCount(0)
    expect(api.all('get', externalLoginsPaths.ofUser(id))).toHaveLength(0)
  })
})
