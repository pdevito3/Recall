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
  // What the discovery document of the provider lists. Each value is a fact about the provider, and the Server uses
  // none of them to log in, so a test fails when a provider upgrade adds or drops one.
  capabilities: IdpCapabilities
}

export interface IdpCapabilities {
  // revocation_endpoint (RFC 7009)
  revocation: boolean
  // authorization_response_iss_parameter_supported (RFC 9207)
  issuerParameter: boolean
  // backchannel_logout_supported
  backChannelLogout: boolean
  // end_session_endpoint
  endSession: boolean
  // pushed_authorization_request_endpoint (RFC 9126)
  pushedAuthorization: boolean
  // dpop_signing_alg_values_supported (RFC 9449). Keycloak lists it because the AppHost starts it with --features=dpop.
  dpop: boolean
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
