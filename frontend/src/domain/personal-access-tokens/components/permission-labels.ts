import type { PersonalAccessTokenDto } from '@/api/generated/models/types/personal-access-token-dto'
import type { PersonalAccessTokenForCreationDtoPermissionsEnumKey } from '@/api/generated/models/types/personal-access-token-for-creation-dto'

export type TokenPermission = PersonalAccessTokenForCreationDtoPermissionsEnumKey

/** The words of each permission, as `Permission.Words` on the Server names it, in the order that the form lists them. */
export const PERMISSION_WORDS: Record<TokenPermission, string> = {
  read_users: 'see the users',
  manage_users: 'manage users',
  manage_user_access: 'manage the access of users',
  read_agents: 'see the agents',
  manage_agents: 'manage agents',
  read_audit_log: 'read the audit log',
  read_audit_log_sensitive: 'read the sensitive values of the audit log',
  manage_tenant: 'manage the tenant',
  manage_model_providers: 'manage the model providers',
}

const PERMISSION_ORDER = Object.keys(PERMISSION_WORDS)

export const isTokenPermission = (value: string): value is TokenPermission => PERMISSION_ORDER.includes(value)

export function inFormOrder(permissions: readonly string[]): TokenPermission[] {
  return PERMISSION_ORDER.filter((permission) => permissions.includes(permission)).filter(isTokenPermission)
}

export const permissionLabel = (permission: TokenPermission): string => {
  const words = PERMISSION_WORDS[permission]
  return words.charAt(0).toUpperCase() + words.slice(1)
}

export function journalAccessWords(token: Pick<PersonalAccessTokenDto, 'readJournal' | 'writeJournal'>): string {
  if (token.writeJournal) return 'Reads and changes the journal'
  if (token.readJournal) return 'Reads the journal'
  return 'No access to the journal'
}

export function permissionsWords(permissions: readonly string[]): string {
  const words = inFormOrder(permissions).map((permission) => PERMISSION_WORDS[permission])
  return words.length === 0 ? 'No other permissions' : `Can also ${words.join(', ')}`
}
