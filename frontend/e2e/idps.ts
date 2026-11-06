/**
 * The login form of each IdP. Each IdP is one JSON file in e2e/idps/, named after the AuthProvider of the AppHost in
 * lowercase. To delete a provider, delete its file and its folder in the AppHost.
 */
import { readdirSync, readFileSync } from 'node:fs'

export interface IdpConfig {
  name: string
  // The Aspire resource of the IdP. A provider outside the stack, such as the Duende demo, has none.
  resource?: string
  email?: string
  username?: string
  // A passwordless IdP, such as 0factor, has no password and no password field
  password?: string
  // Selectors for login form (vary by IDP)
  usernameSelector: string
  passwordSelector?: string
  submitSelector: string
  // Some IDPs need special handling
  needsOtpSkip?: boolean
  // Authentik uses a two-step login (username first, then password)
  twoStepLogin?: boolean
  // Second step submit selector for two-step login (if different from submitSelector)
  secondStepSubmitSelector?: string
  // The button of a page that asks the person to confirm a logout with no id_token_hint
  logoutConfirmSelector?: string
  // A provider that stays on its own page after a logout with no id_token_hint
  logoutStaysAtProvider?: boolean
  capabilities: IdpCapabilities
}

export interface IdpCapabilities {
  revocation: boolean
  issuerParameter: boolean
  backChannelLogout: boolean
  endSession: boolean
  pushedAuthorization: boolean
}

const idpsDir = new URL('./idps/', import.meta.url)

export function idpNames(): string[] {
  return readdirSync(idpsDir)
    .filter((file) => file.endsWith('.json'))
    .map((file) => file.slice(0, -'.json'.length))
}

export function loadIdp(name: string | undefined): IdpConfig {
  if (!name) {
    throw new Error('IDP is not set. Run the tests with `pnpm test:e2e:stack`, which finds the IdP of the running stack.')
  }
  const key = name.toLowerCase()
  if (!idpNames().includes(key)) {
    throw new Error(`IDP '${name}' has no file in e2e/idps/. Use one of these: ${idpNames().join(', ')}.`)
  }
  return JSON.parse(readFileSync(new URL(`${key}.json`, idpsDir), 'utf8')) as IdpConfig
}
