import type { InitiateUserAndTenantResponseDto } from '@/api/generated/models/types/initiate-user-and-tenant-response-dto'
import {
  type UserDto,
  type UserDtoAllowedActionsEnumKey,
  type UserDtoRolesEnumKey,
  userDtoRolesEnum,
} from '@/api/generated/models/types/user-dto'

let seed = 0
const next = () => ++seed

const ADMIN_ACTIONS: UserDtoAllowedActionsEnumKey[] = ['Update', 'AddRole', 'RemoveRole', 'AddPermission', 'RemovePermission', 'Delete']

class UserBuilder {
  private readonly user: UserDto
  private readonly givable: UserDtoRolesEnumKey[] | undefined

  private constructor(user: UserDto, givable?: UserDtoRolesEnumKey[]) {
    this.user = user
    this.givable = givable
  }

  static create() {
    return new UserBuilder({
      id: `usr_${next()}`,
      tenantId: 'tnt_1',
      firstName: 'Bob',
      lastName: 'Smith',
      fullName: 'Bob Smith',
      identifier: '1',
      email: 'bob@example.com',
      username: 'bob',
      roles: [],
      permissions: [],
      addedPermissions: [],
      removedPermissions: [],
      allowedActions: [],
      addableRoles: [],
      version: 1,
    })
  }

  with(patch: Partial<UserDto>) {
    return new UserBuilder({ ...this.user, ...patch }, this.givable)
  }

  /** The `sub` claim of the user. */
  identifiedBy(identifier: string) {
    return this.with({ identifier })
  }

  named(fullName: string) {
    const [firstName, ...rest] = fullName.split(' ')
    const username = firstName.toLowerCase()
    return this.with({ firstName, lastName: rest.join(' '), fullName, username, email: `${username}@example.com` })
  }

  inRoles(...roles: UserDtoRolesEnumKey[]) {
    return this.with({ roles })
  }

  seenByAdmin() {
    return this.with({ allowedActions: ADMIN_ACTIONS })
  }

  allowing(...allowedActions: UserDtoAllowedActionsEnumKey[]) {
    return this.with({ allowedActions })
  }

  savedAgain() {
    return this.with({ version: this.user.version + 1 })
  }

  /** The roles that the caller can give. Without it, a caller who may add a role can give each one. */
  givingOnly(...roles: UserDtoRolesEnumKey[]) {
    return new UserBuilder(this.user, roles)
  }

  /** As the Server does, the addable roles are the ones the caller can give and the user does not have. */
  build(): UserDto {
    const givable = this.givable ?? Object.values(userDtoRolesEnum)
    const addableRoles = this.user.allowedActions.includes('AddRole') ? givable.filter((r) => !this.user.roles.includes(r)) : []
    return { ...this.user, addableRoles }
  }
}

export const aUser = () => UserBuilder.create()

export const aStartedTenant = (name: string, user = aUser()): InitiateUserAndTenantResponseDto => {
  const admin = { ...user.build(), roles: ['Admin' as const] }
  return { tenant: { id: admin.tenantId, name, version: 1 }, user: admin }
}
