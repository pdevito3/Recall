import { userDtoRolesEnum, type UserDtoRolesEnumKey } from '@/api/generated/models/types/user-dto'

export type UserRole = UserDtoRolesEnumKey

export const USER_ROLES = Object.values(userDtoRolesEnum)

export const ROLE_LABELS: Record<UserRole, string> = {
  Admin: 'Admin',
  UserManager: 'User manager',
  Auditor: 'Auditor',
}

export const isUserRole = (value: unknown): value is UserRole => USER_ROLES.some((role) => role === value)

/** The roles as a person reads them. A user with no role is a Member, who has only their own journal. */
export const rolesWords = (roles: readonly UserRole[]): string =>
  roles.length === 0 ? 'Member' : roles.map((role) => ROLE_LABELS[role]).join(', ')
