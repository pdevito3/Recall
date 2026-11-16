import type { UserSettingDto } from '@/api/generated/models/types/user-setting-dto'
import type { MockRoute } from './mock-route'

export const settingsPaths = { mine: '/api/v1/me/settings', deviceTimeZone: '/api/v1/me/settings/device-time-zone' }

export const aSetting = (patch: Partial<UserSettingDto> = {}): UserSettingDto => ({
  timeZoneMode: 'Fixed',
  timeZone: 'UTC',
  timeZoneIsKnown: true,
  locationCaptureEnabled: false,
  requirePeopleProposalApproval: false,
  removeFillerWords: true,
  writeNumbersAsDigits: true,
  version: 1,
  ...patch,
})

export const settingsApi = {
  are(setting: UserSettingDto): MockRoute {
    return { method: 'get', path: settingsPaths.mine, status: 200, body: setting }
  },

  saves(setting: UserSettingDto): MockRoute {
    return { method: 'put', path: settingsPaths.mine, status: 200, body: setting }
  },

  takesTheDeviceZone(setting: UserSettingDto): MockRoute {
    return { method: 'put', path: settingsPaths.deviceTimeZone, status: 200, body: setting }
  },
}
