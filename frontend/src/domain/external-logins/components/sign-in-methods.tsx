import { useRef } from 'react'

import { Button } from '@/components/ui/button'
import { useLogout } from '@/domain/auth/apis/auth-actions'
import { isProblemDetailsError, SIGN_IN_AGAIN_TO_LINK_PROBLEM_TYPE } from '@/lib/problem-details'

import { useLoginProviderModes, useMyExternalLogins, useStartLink } from '../apis/external-logins'
import { ExternalLoginList } from './external-login-list'

/** The result of a link that the Server gives in the query when the provider comes back to settings. */
export type LinkResult = 'linked' | 'conflict' | 'refused' | 'failed'

const resultWords: Record<LinkResult, string> = {
  linked: 'Recall linked the provider. You can sign in with it now.',
  conflict: 'This account at the provider is linked to another person in Recall.',
  refused: 'Recall did not link the provider, because the link started in another browser or session. Start it again from settings.',
  failed: 'The provider did not finish the link. Try again.',
}

export function SignInMethods({ linkResult }: { linkResult?: LinkResult }) {
  const links = useMyExternalLogins()
  const providers = useLoginProviderModes()
  const start = useStartLink()
  const logout = useLogout()
  const headingRef = useRef<HTMLHeadingElement>(null)

  const linked = new Set(links.data?.map((link) => link.providerId))
  const linkable = links.isSuccess ? (providers.data ?? []).filter((mode) => mode.state === 'ready' && !linked.has(mode.id)) : []
  // A session that began more than 15 minutes ago gets this 403 (IDP-10), and only a new sign-in gives a fresh one. Another 403, such
  // as the one of a native session, gets its own message and no sign-out.
  const needsFreshSignIn = isProblemDetailsError(start.error) && start.error.problemDetails.type === SIGN_IN_AGAIN_TO_LINK_PROBLEM_TYPE

  return (
    <section className="flex flex-col gap-3" aria-labelledby="settings-sign-in-methods">
      <h2 id="settings-sign-in-methods" ref={headingRef} tabIndex={-1} className="text-lg font-semibold">Sign-in methods</h2>
      <p className="text-sm text-muted-foreground">Link an account at a provider, and sign in to Recall with it.</p>
      {linkResult && (
        <p
          role={linkResult === 'linked' ? 'status' : 'alert'}
          className={
            linkResult === 'linked'
              ? 'rounded-lg border p-3 text-sm wrap-anywhere'
              : 'rounded-lg border border-destructive/20 bg-destructive/10 p-3 text-sm text-destructive wrap-anywhere'
          }
        >
          {resultWords[linkResult]}
        </p>
      )}
      <ExternalLoginList
        links={links}
        label="Your sign-in methods"
        emptyText="You have no linked provider yet."
        headingRef={headingRef}
      />
      {start.isError && (
        <div role="alert" className="flex flex-col items-start gap-3 rounded-lg border border-destructive/20 bg-destructive/10 p-3 text-sm text-destructive wrap-anywhere">
          <p>{isProblemDetailsError(start.error) ? start.error.displayMessage : 'Recall could not start the link. Try again.'}</p>
          {needsFreshSignIn && (
            <Button variant="outline" isPending={logout.isPending} onPress={() => logout.mutate()}>
              Sign out and sign in again
            </Button>
          )}
        </div>
      )}
      {linkable.length > 0 && (
        <div className="flex flex-wrap gap-2">
          {linkable.map((mode) => (
            <Button
              key={mode.id}
              variant="outline"
              isPending={start.isPending && start.variables === mode.id}
              isDisabled={start.isPending}
              onPress={() => start.mutate(mode.id)}
            >
              Link {mode.displayName}
            </Button>
          ))}
        </div>
      )}
    </section>
  )
}
