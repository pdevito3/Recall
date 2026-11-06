import { aSetting, settingsApi, settingsPaths } from '@tests/support/api/settings-api'
import { usersApi } from '@tests/support/api/users-api'
import { aProblem } from '@tests/support/builders/problem-details-builder'
import { expect, test } from '@tests/support/fixtures'
import { settingsPage } from './settings.tasks'

test.describe('the settings of a person', () => {
  test('sees the name that the Server reads from the access token', async ({ page, api }) => {
    api.mock(usersApi.currentIs({ identifier: '1', name: 'Robert Smith' }))
    const settings = settingsPage(page)

    await settings.open()

    await expect(settings.at.signedInAs('Robert Smith')).toBeVisible()
  })

  test('sees the identifier when the token has no name', async ({ page, api }) => {
    api.mock(usersApi.currentIs({ identifier: 'user-1', name: null }))
    const settings = settingsPage(page)

    await settings.open()

    await expect(settings.at.signedInAs('user-1')).toBeVisible()
  })

  test('sees an error when the Server fails to load the user', async ({ page, api }) => {
    api.mock(usersApi.currentFails(aProblem().saying('The Server is down.')))
    const settings = settingsPage(page)

    await settings.open()

    await expect(settings.at.userFailed).toBeVisible()
  })

  test('sends a change with the other values and a time', async ({ page, api }) => {
    api.mock(settingsApi.are(aSetting({ timeZone: 'America/Chicago' })), settingsApi.saves(aSetting({ timeZone: 'America/Chicago', locationCaptureEnabled: true })))
    const settings = settingsPage(page)
    await settings.open()

    await settings.turnOnLocation()

    const body = api.last('put', settingsPaths.mine)?.body as { timeZone: string; locationCaptureEnabled: boolean; savedAt: string }
    expect(body.timeZone).toBe('America/Chicago')
    expect(body.locationCaptureEnabled).toBe(true)
    expect(new Date(body.savedAt).getTime()).not.toBeNaN()
    await expect(settings.at.saved).toBeVisible()
  })

  test('says so when the Server does not know the saved zone', async ({ page, api }) => {
    api.mock(settingsApi.are(aSetting({ timeZone: 'Mars/Olympus', timeZoneIsKnown: false })))
    const settings = settingsPage(page)

    await settings.open()

    await expect(settings.at.unknownZone).toBeVisible()
  })
})
