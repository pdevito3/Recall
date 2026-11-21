import { as } from '@tests/support/auth/actors'
import { expect, test } from '@tests/support/fixtures'
import { settingsPage } from './settings.tasks'

const changePath = '/auth/password/change'
const current = 'the current journal passphrase'
const next = 'a new journal passphrase for me'

const withPassword = () => {
  const actor = as.user()
  return actor.signedIn ? { ...actor, user: { ...actor.user, login_name: 'bob' } } : actor
}

test.describe('the change of a password', () => {
  test('a person with no password sees no password form', { tag: '@PWD-17' }, async ({ page }) => {
    const settings = settingsPage(page)

    await settings.open()

    await expect(settings.at.passwordHeading).toHaveCount(0)
    await expect(settings.at.currentPassword).toHaveCount(0)
  })

  test.describe('a person with a password', () => {
    test.use({ actor: withPassword() })

    test('changes the password with the current one', { tag: '@PWD-17' }, async ({ page, api }) => {
      api.mock({ method: 'post', path: changePath, status: 204 })
      const settings = settingsPage(page)
      await settings.open()

      await settings.changePassword(current, next)

      await expect(settings.at.passwordChanged).toBeVisible()
      const sent = api.last('post', changePath)
      const form = new URLSearchParams(sent?.body as string)
      expect(form.get('currentPassword')).toBe(current)
      expect(form.get('newPassword')).toBe(next)
      expect(sent?.headers.get('x-csrf')).toBe('1')
      await expect(settings.at.currentPassword).toHaveValue('')
    })

    test('sees the refusal of a wrong current password at its field', { tag: '@PWD-17' }, async ({ page, api }) => {
      const wrong = 'Your current password is wrong.'
      api.mock({ method: 'post', path: changePath, status: 403, body: { status: 403, detail: wrong, errors: { currentPassword: [wrong] } } })
      const settings = settingsPage(page)
      await settings.open()

      await settings.changePassword('a wrong current passphrase', next)

      await expect(settings.at.message(wrong)).toBeVisible()
      await expect(settings.at.currentPassword).toHaveAttribute('aria-invalid', 'true')
    })
  })
})
