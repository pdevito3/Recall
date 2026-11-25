import type { PersonalAccessTokenCreatedDto } from '@/api/generated/models/types/personal-access-token-created-dto'
import type {
  PersonalAccessTokenDto,
  PersonalAccessTokenDtoPermissionsEnumKey,
} from '@/api/generated/models/types/personal-access-token-dto'

let seed = 0
const next = () => ++seed

class PersonalAccessTokenBuilder {
  private readonly token: PersonalAccessTokenDto

  private constructor(token: PersonalAccessTokenDto) {
    this.token = token
  }

  static create() {
    const n = next()
    return new PersonalAccessTokenBuilder({
      id: `pat_${n}`,
      userId: 'usr_1',
      name: `Token ${n}`,
      permissions: [],
      readJournal: false,
      writeJournal: false,
      jobTools: false,
      createdAt: '2026-11-01T12:00:00Z',
      expiresAt: '2027-01-30T12:00:00Z',
      lastUsedAt: null,
      revokedAt: null,
      allowedActions: ['Revoke'],
    })
  }

  private with(patch: Partial<PersonalAccessTokenDto>) {
    return new PersonalAccessTokenBuilder({ ...this.token, ...patch })
  }

  named(name: string) {
    return this.with({ name })
  }

  of(userId: string) {
    return this.with({ userId })
  }

  granting(...permissions: PersonalAccessTokenDtoPermissionsEnumKey[]) {
    return this.with({ permissions })
  }

  readingTheJournal() {
    return this.with({ readJournal: true })
  }

  changingTheJournal() {
    return this.with({ readJournal: true, writeJournal: true })
  }

  withTheJobTools() {
    return this.with({ jobTools: true })
  }

  neverExpiring() {
    return this.with({ expiresAt: null })
  }

  expiringAt(expiresAt: string) {
    return this.with({ expiresAt })
  }

  usedAt(lastUsedAt: string) {
    return this.with({ lastUsedAt })
  }

  revokedAt(revokedAt: string) {
    return this.with({ revokedAt, allowedActions: [] })
  }

  notRevocable() {
    return this.with({ allowedActions: [] })
  }

  build(): PersonalAccessTokenDto {
    return this.token
  }

  /** The answer of a create, the one answer that holds the value. */
  created(value = `rcl_pat_${'A'.repeat(43)}`): PersonalAccessTokenCreatedDto {
    return { token: this.token, value }
  }
}

export const aToken = () => PersonalAccessTokenBuilder.create()
