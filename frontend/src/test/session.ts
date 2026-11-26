import type { UserDto } from '@/api/generated/models/types/user-dto'
import type { UserSettingDto } from '@/api/generated/models/types/user-setting-dto'
import type { AuthUser } from '@/domain/auth/types'

import type { Answer } from './answer'

export const member: UserDto = {
  id: 'usr_0199f0c1a2b37c4d8e9f0a1b2c3d4e5f',
  tenantId: 'ten_0199f0c1a2b37c4d8e9f0a1b2c3d4e5f',
  firstName: 'Bob',
  lastName: 'Smith',
  fullName: 'Bob Smith',
  identifier: 'bob',
  email: 'bob@example.com',
  username: 'bob',
  roles: [],
  permissions: [],
  addedPermissions: [],
  removedPermissions: [],
  addableRoles: [],
  allowedActions: [],
  version: 1,
}

export const memberSession: AuthUser = { sub: member.identifier, name: member.fullName, sid: 'test-session', client_kind: 'web' }

export const memberSetting: UserSettingDto = { timeZoneMode: 'Fixed', timeZone: 'UTC', timeZoneIsKnown: true, locationCaptureEnabled: false, requirePeopleProposalApproval: false, removeFillerWords: true, writeNumbersAsDigits: true, version: 1 }

export const signedInMember: Answer[] = [
  { method: 'GET', path: '/auth/me', body: memberSession },
  { method: 'GET', path: `/api/v1/users/by-identifier/${member.identifier}`, body: member },
  { method: 'GET', path: '/api/v1/file-store/status', body: { canWrite: true } },
  { method: 'GET', path: '/api/v1/me/settings', body: memberSetting },
]
