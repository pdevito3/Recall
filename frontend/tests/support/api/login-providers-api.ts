import type { EnvironmentLoginProviderDto } from '@/api/generated/models/types/environment-login-provider-dto'
import type { LoginProviderDto } from '@/api/generated/models/types/login-provider-dto'
import type { LoginProviderListDto } from '@/api/generated/models/types/login-provider-list-dto'
import type { LoginProviderTestResultDto } from '@/api/generated/models/types/login-provider-test-result-dto'
import type { LoginSettingsDto, LoginSettingsDtoPasswordLoginEnumKey } from '@/api/generated/models/types/login-settings-dto'

import { aProblem } from '../builders/problem-details-builder'
import type { MockRoute } from './mock-route'

export const loginProviderPaths = {
  list: '/api/v1/login-providers',
  test: '/api/v1/login-providers/test',
  record: (id: string) => `/api/v1/login-providers/${id}`,
  settings: '/api/v1/login-settings',
}

export const callbackUrl = 'https://recall.example.com/auth/oidc/callback'

export const aLoginProvider = (changes: Partial<LoginProviderDto> = {}): LoginProviderDto => ({
  id: 'home-idp',
  displayName: 'Home IdP',
  issuer: 'https://id.example.com/realms/home',
  clientId: 'recall',
  allowHttp: false,
  caCertificatePath: null,
  state: 'Usable',
  secretState: 'Readable',
  createdAt: '2026-11-20T10:00:00Z',
  version: 1,
  ...changes,
})

export const anEnvironmentProvider = (changes: Partial<EnvironmentLoginProviderDto> = {}): EnvironmentLoginProviderDto => ({
  id: 'oidc0',
  displayName: 'Company SSO',
  issuer: 'https://sso.example.com',
  ...changes,
})

export const loginProvidersApi = {
  listHolds(items: LoginProviderDto[], environmentProviders: EnvironmentLoginProviderDto[] = []): MockRoute {
    return {
      method: 'get',
      path: loginProviderPaths.list,
      status: 200,
      body: { items, environmentProviders, callbackUrl } satisfies LoginProviderListDto,
    }
  },

  addAnswers(provider: LoginProviderDto): MockRoute {
    return { method: 'post', path: loginProviderPaths.list, status: 200, body: provider }
  },

  addRefuses(errors: Record<string, string[]>): MockRoute {
    return { method: 'post', path: loginProviderPaths.list, status: 422, body: aProblem().invalid(errors).build() }
  },

  /** A 409 as `ConflictException` gives it: the detail, and the message on its field. */
  listFails(): MockRoute {
    return { method: 'get', path: loginProviderPaths.list, status: 500, body: aProblem().build() }
  },

  addConflicts(field: string, detail: string): MockRoute {
    return { method: 'post', path: loginProviderPaths.list, status: 409, body: { ...aProblem().conflict(detail).build(), errors: { [field]: [detail] } } }
  },

  updateAnswers(provider: LoginProviderDto): MockRoute {
    return { method: 'put', path: loginProviderPaths.record(provider.id), status: 200, body: provider }
  },

  updateIsStale(provider: LoginProviderDto): MockRoute {
    return { method: 'put', path: loginProviderPaths.record(provider.id), status: 409, body: aProblem().staleVersion().build() }
  },

  deleteWorks(provider: LoginProviderDto): MockRoute {
    return { method: 'delete', path: loginProviderPaths.record(provider.id), status: 204 }
  },

  deleteConflicts(provider: LoginProviderDto, detail: string): MockRoute {
    return { method: 'delete', path: loginProviderPaths.record(provider.id), status: 409, body: aProblem().conflict(detail).build() }
  },

  testAnswers(result: LoginProviderTestResultDto): MockRoute {
    return { method: 'post', path: loginProviderPaths.test, status: 200, body: result }
  },

  settingsHold(passwordLogin: LoginSettingsDtoPasswordLoginEnumKey = 'On', version = 1): MockRoute {
    return { method: 'get', path: loginProviderPaths.settings, status: 200, body: { passwordLogin, version } satisfies LoginSettingsDto }
  },

  settingsUpdateAnswers(passwordLogin: LoginSettingsDtoPasswordLoginEnumKey, version: number): MockRoute {
    return { method: 'put', path: loginProviderPaths.settings, status: 200, body: { passwordLogin, version } satisfies LoginSettingsDto }
  },

  /** The 409 of IDP-13, as `ConflictException` gives it with no field. */
  settingsUpdateConflicts(detail: string): MockRoute {
    return { method: 'put', path: loginProviderPaths.settings, status: 409, body: aProblem().conflict(detail).build() }
  },

  settingsUpdateIsStale(): MockRoute {
    return { method: 'put', path: loginProviderPaths.settings, status: 409, body: aProblem().staleVersion().build() }
  },
}
