import type { UserSettingDto } from '@/api/generated/models/types/user-setting-dto'
import type { MockRoute } from './mock-route'

export const settingsPaths = { mine: '/api/v1/me/settings' }

export const aSetting = (patch: Partial<UserSettingDto> = {}): UserSettingDto => ({
  timeZone: 'UTC',
  timeZoneIsKnown: true,
  locationCaptureEnabled: false,
  requirePeopleProposalApproval: false,
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
}
