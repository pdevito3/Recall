import { Button } from '@/components/ui/button'

import { useMembership } from '../apis/get-membership'

/** Says that the check of the user record of the signed-in user failed. */
export function MembershipNotice() {
  const membership = useMembership()

  if (membership.status !== 'error') return null

  return (
    <section
      aria-label="Membership check"
      className="mb-4 flex flex-wrap items-center justify-between gap-3 border-y border-destructive/20 bg-destructive/10 px-4 py-3 text-sm text-destructive"
    >
      <p className="min-w-0">{membership.message}</p>
      {membership.retry && <Button variant="outline" size="sm" onPress={membership.retry}>Try Again</Button>}
    </section>
  )
}
