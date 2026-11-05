/**
 * Runs the Playwright e2e tests (playwright.stack.config.ts) against the Aspire stack of this checkout.
 * An isolated stack has random ports, so this script reads the URLs from `dotnet aspire describe`.
 * The IDP is the one whose resource is in the stack, or the Duende demo if the stack has no IdP resource.
 * Set IDP to override it. Extra arguments go to `playwright test`, for example `pnpm test:e2e:stack --repeat-each=5`.
 * See docs/authentication.md for the command that starts the stack.
 */
import { spawnSync } from 'node:child_process'
import { idpNames, loadIdp } from './idps.ts'
import { checkStack } from './stack-description.ts'

const appHost = '../Recall.AppHost'

// Aspire writes its messages to stderr, so the user sees them above the message of the script
const stack = checkStack({
  run: spawnSync('dotnet', ['aspire', 'describe', '--format', 'Json', '--apphost', appHost], {
    encoding: 'utf8',
    stdio: ['ignore', 'pipe', 'inherit'],
  }),
  appHost,
})
if (stack.kind === 'not-ready') {
  fail(stack.message)
}
const described = { resources: stack.resources }

// A problem with the stack is a message to the user, not a stack trace
function fail(message: string): never {
  console.error(message)
  process.exit(1)
}

function environmentOf(resource: string): Record<string, string> {
  return (described.resources.find((r) => r.displayName === resource) as { environment?: Record<string, string> } | undefined)
    ?.environment ?? {}
}

function serverAuthority(): string {
  const authority = environmentOf('server').Auth__Authority
  if (!authority) {
    fail('The stack has no Auth__Authority for the server. Start the stack with the AuthProvider of the IDP.')
  }
  return authority
}

function endpointUrl(resource: string, endpoint: string): string {
  const url = described.resources
    .find((r) => r.displayName === resource)
    ?.urls?.find((u) => u.name === endpoint)?.url
  if (!url) {
    fail(`The stack has no ${endpoint} URL for '${resource}'. Start the stack with the AuthProvider of the IDP.`)
  }
  return url
}

function idpOfStack(): string {
  const resources = new Set(described.resources.map((r) => r.displayName))
  const idps = idpNames().map((name) => ({ name, resource: loadIdp(name).resource }))
  const detected =
    idps.find((idp) => idp.resource !== undefined && resources.has(idp.resource)) ??
    idps.find((idp) => idp.resource === undefined)
  if (!detected) {
    fail(`The stack has no resource of an IDP in e2e/idps/. Set IDP to one of these: ${idpNames().join(', ')}.`)
  }
  return detected.name
}

const idp = (process.env.IDP ?? idpOfStack()).toLowerCase()
const env: NodeJS.ProcessEnv = {
  ...process.env,
  IDP: idp,
  BASE_URL: endpointUrl('webfrontend', 'http'),
  SERVER_URL: endpointUrl('server', 'https'),
  AUTHORITY: serverAuthority(),
}

console.log(`BASE_URL=${env.BASE_URL} SERVER_URL=${env.SERVER_URL} AUTHORITY=${env.AUTHORITY} IDP=${env.IDP}`)

const result = spawnSync('pnpm', ['exec', 'playwright', 'test', '--config', 'playwright.stack.config.ts', ...process.argv.slice(2)], { stdio: 'inherit', env })
process.exit(result.status ?? 1)
