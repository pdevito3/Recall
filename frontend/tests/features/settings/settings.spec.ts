import { aSetting, settingsApi, settingsPaths } from '@tests/support/api/settings-api'
import { usersApi } from '@tests/support/api/users-api'
import { aProblem } from '@tests/support/builders/problem-details-builder'
import { expect, test } from '@tests/support/fixtures'
import { useDefaultFontSize } from '@tests/support/screen'
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

    expect(api.last('put', settingsPaths.mine)?.body).toMatchObject({ timeZoneMode: 'FollowDevice', timeZone: null })
  })

  test('finds a zone by its name with a space, and says so when no zone matches', async ({ page, api }) => {
    api.mock(settingsApi.are(aSetting({ timeZoneMode: 'Fixed', timeZone: 'Asia/Tokyo' })))
    const settings = settingsPage(page)
    await settings.open()

    await settings.at.zone.fill('new york')
    await expect(settings.at.zoneOptions).toHaveText([/^America \/ New_York \(/])

    await settings.at.zone.fill('nowhere at all')
    await expect(settings.at.noZoneMatches).toBeVisible()
  })

  test('shows the full name of the zone in use on a phone with large text', async ({ page, api }) => {
    await page.setViewportSize({ width: 390, height: 844 })
    await useDefaultFontSize(page, 24)
    const zone = test.info().project.use.timezoneId!
    api.mock(settingsApi.are(aSetting({ timeZoneMode: 'FollowDevice', timeZone: zone })))
    const settings = settingsPage(page)

    await settings.open()

    await expect(settings.at.zoneInUse).toContainText(zone.replaceAll('/', ' / '))
    expect(await settings.isInsideTheWindow(settings.at.zoneInUse)).toBe(true)
    expect(await settings.sideScrollRange()).toBe(0)
  })

  test('a browser that cannot name its zone still shows the page, follows the device, and saves a picked zone', async ({ page, api }) => {
    await settingsPage(page).hideTheZoneOfTheBrowser()
    api.mock(
      settingsApi.are(aSetting({ timeZoneMode: 'FollowDevice', timeZone: 'America/Chicago' })),
      settingsApi.saves(aSetting({ timeZoneMode: 'Fixed', timeZone: 'Asia/Tokyo', version: 2 })),
    )
    const settings = settingsPage(page)
    await settings.open()

    await expect(settings.at.zone).toHaveValue('Automatic')
    await settings.pickZone('tokyo', /^Asia \/ Tokyo \(/)

    expect(api.last('put', settingsPaths.mine)?.body).toMatchObject({ timeZoneMode: 'Fixed', timeZone: 'Asia/Tokyo' })
    expect(api.all('put', settingsPaths.deviceTimeZone)).toHaveLength(0)
  })

  test('a pick of Automatic after a fixed zone reports the zone of the browser again', async ({ page, api }) => {
    const zone = test.info().project.use.timezoneId!
    api.mock(
      settingsApi.are(aSetting({ timeZoneMode: 'FollowDevice', timeZone: 'Antarctica/Troll' })),
      settingsApi.takesTheDeviceZone(aSetting({ timeZoneMode: 'FollowDevice', timeZone: zone, version: 2 })),
      settingsApi.saves(aSetting({ timeZoneMode: 'FollowDevice', timeZone: 'Asia/Tokyo', version: 4 })),
      { ...settingsApi.saves(aSetting({ timeZoneMode: 'Fixed', timeZone: 'Asia/Tokyo', version: 3 })), once: true },
    )
    const settings = settingsPage(page)
    await settings.open()
    await expect.poll(() => api.all('put', settingsPaths.deviceTimeZone)).toHaveLength(1)

    await settings.pickZone('tokyo', /^Asia \/ Tokyo \(/)
    await settings.pickZone('auto', /^Automatic/)

    await expect.poll(() => api.all('put', settingsPaths.deviceTimeZone)).toHaveLength(2)
    expect(api.last('put', settingsPaths.deviceTimeZone)?.body).toEqual({ timeZone: zone })
  })

  test('a page other than the journal reports the zone of the browser while the setting follows the device', async ({ page, api }) => {
    const zone = test.info().project.use.timezoneId!
    api.mock(
      settingsApi.are(aSetting({ timeZoneMode: 'FollowDevice', timeZone: 'Antarctica/Troll' })),
      settingsApi.takesTheDeviceZone(aSetting({ timeZoneMode: 'FollowDevice', timeZone: zone, version: 2 })),
    )
    const settings = settingsPage(page)

    await settings.open()

    await expect.poll(() => api.all('put', settingsPaths.deviceTimeZone)).toHaveLength(1)
    expect(api.last('put', settingsPaths.deviceTimeZone)?.body).toEqual({ timeZone: zone })
  })

  test('says so when the Server does not know the saved zone', async ({ page, api }) => {
    api.mock(settingsApi.are(aSetting({ timeZone: 'Mars/Olympus', timeZoneIsKnown: false })))
    const settings = settingsPage(page)

    await settings.open()

    await expect(settings.at.unknownZone).toBeVisible()
  })
})
