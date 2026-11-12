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

  test('a long name stays in the page on a phone', async ({ page, api }) => {
    await page.setViewportSize({ width: 390, height: 844 })
    const name = 'n'.repeat(200)
    api.mock(usersApi.currentIs({ identifier: '1', name }))
    const settings = settingsPage(page)

    await settings.open()

    await expect(settings.at.signedInAs(name)).toBeVisible()
    expect(await settings.sideScrollRange()).toBe(0)
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

    const body = api.last('put', settingsPaths.mine)?.body as { timeZoneMode: string; timeZone: string; locationCaptureEnabled: boolean; version: number; savedAt?: string }
    expect(body.timeZoneMode).toBe('Fixed')
    expect(body.timeZone).toBe('America/Chicago')
    expect(body.locationCaptureEnabled).toBe(true)
    expect(body.version).toBe(aSetting().version)
    expect(body.savedAt).toBeUndefined()
    await expect(settings.at.saved).toBeVisible()
  })

  for (const [saved, shown] of [['UTC', /^UTC \(/], ['Asia/Kolkata', /^Asia \/ Kolkata \(/]] as const) {
    test(`shows the saved zone ${saved}, which the list of the browser can leave out`, async ({ page, api }) => {
      api.mock(settingsApi.are(aSetting({ timeZone: saved })))
      const settings = settingsPage(page)

      await settings.open()

      await expect(settings.at.zone).toHaveValue(shown)
    })
  }

  test('shows Automatic with the zone of this browser while the setting follows the device', async ({ page, api }) => {
    const zone = test.info().project.use.timezoneId!
    api.mock(settingsApi.are(aSetting({ timeZoneMode: 'FollowDevice', timeZone: zone })))
    const settings = settingsPage(page)

    await settings.open()

    await expect(settings.at.zone).toHaveValue(`Automatic (${zone.replaceAll('/', ' / ')})`)
  })

  test('picks a fixed zone from a search of the list', async ({ page, api }) => {
    const zone = test.info().project.use.timezoneId!
    api.mock(
      settingsApi.are(aSetting({ timeZoneMode: 'FollowDevice', timeZone: zone })),
      settingsApi.saves(aSetting({ timeZoneMode: 'Fixed', timeZone: 'Asia/Tokyo', version: 2 })),
    )
    const settings = settingsPage(page)
    await settings.open()

    await settings.at.zone.fill('tokyo')
    await expect(settings.at.zoneOptions).toHaveText([/^Asia \/ Tokyo \(/])
    await settings.pickZone('tokyo', /^Asia \/ Tokyo \(/)

    expect(api.last('put', settingsPaths.mine)?.body).toMatchObject({ timeZoneMode: 'Fixed', timeZone: 'Asia/Tokyo' })
    await expect(settings.at.zone).toHaveValue(/^Asia \/ Tokyo \(/)
  })

  test('picks Automatic, the first option, to follow the device again', async ({ page, api }) => {
    const zone = test.info().project.use.timezoneId!
    api.mock(
      settingsApi.are(aSetting({ timeZoneMode: 'Fixed', timeZone: 'Asia/Tokyo' })),
      settingsApi.saves(aSetting({ timeZoneMode: 'FollowDevice', timeZone: zone, version: 2 })),
    )
    const settings = settingsPage(page)
    await settings.open()

    const names = await settings.zoneNamesInOrder()
    expect(names[0]).toBe(`Automatic (${zone.replaceAll('/', ' / ')})`)
    await settings.pickZone('auto', /^Automatic/)

    expect(api.last('put', settingsPaths.mine)?.body).toMatchObject({ timeZoneMode: 'FollowDevice', timeZone: zone })
  })

  test('says so when the Server does not know the saved zone', async ({ page, api }) => {
    api.mock(settingsApi.are(aSetting({ timeZone: 'Mars/Olympus', timeZoneIsKnown: false })))
    const settings = settingsPage(page)

    await settings.open()

    await expect(settings.at.unknownZone).toBeVisible()
  })
})
