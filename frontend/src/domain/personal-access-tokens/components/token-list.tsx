import type { UseQueryResult } from '@tanstack/react-query'
import { useState, type RefObject } from 'react'

import type { PersonalAccessTokenDto } from '@/api/generated/models/types/personal-access-token-dto'
import { Notification } from '@/components/notification'
import { QueryError } from '@/components/query-error'
import { Button } from '@/components/ui/button'
import {
  ResponsiveDialog,
  ResponsiveDialogClose,
  ResponsiveDialogDescription,
  ResponsiveDialogFooter,
  ResponsiveDialogHeader,
  ResponsiveDialogTitle,
  ResponsiveDialogTrigger,
} from '@/components/ui/responsive-dialog'
import { browserTimeZone, formatInstant } from '@/lib/zoned-time'

import { useRevokePersonalAccessToken } from '../apis/personal-access-tokens'
import { journalAccessWords, permissionsWords } from './permission-labels'

interface TokenListProps {
  tokens: UseQueryResult<PersonalAccessTokenDto[]>
  label: string
  emptyText: string
  headingRef: RefObject<HTMLHeadingElement | null>
}

const dayOf = (instant: string) =>
  formatInstant(new Date(instant), browserTimeZone(), { year: 'numeric', month: 'short', day: 'numeric' })

const timeOf = (instant: string) =>
  formatInstant(new Date(instant), browserTimeZone(), { year: 'numeric', month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' })

type TokenState = 'Active' | 'Expired' | 'Revoked'

function stateOf(token: PersonalAccessTokenDto, now: number): TokenState {
  if (token.revokedAt) return 'Revoked'
  if (token.expiresAt && new Date(token.expiresAt).getTime() <= now) return 'Expired'
  return 'Active'
}

function expiryWords(token: PersonalAccessTokenDto, state: TokenState): string {
  if (!token.expiresAt) return 'Never expires'
  return state === 'Expired' ? `Expired on ${dayOf(token.expiresAt)}` : `Expires on ${dayOf(token.expiresAt)}`
}

export function TokenList({ tokens, label, emptyText, headingRef }: TokenListProps) {
  const [now] = useState(() => Date.now())

  if (tokens.isPending) {
    return <p role="status" aria-label={`Loading ${label}`} className="text-muted-foreground">Loading the tokens</p>
  }
  if (tokens.isError) {
    return <QueryError title="The tokens did not load." error={tokens.error} onRetry={() => void tokens.refetch()} />
  }
  if (tokens.data.length === 0) {
    return <p className="text-muted-foreground wrap-anywhere">{emptyText}</p>
  }

  return (
    <ul aria-label={label} className="flex flex-col divide-y rounded-lg border">
      {tokens.data.map((token) => {
        const state = stateOf(token, now)
        return (
          <li key={token.id} aria-label={token.name} className="flex flex-wrap items-start justify-between gap-x-3 gap-y-2 p-3">
            <div className="flex min-w-0 flex-1 basis-60 flex-col gap-1 text-sm">
              <div className="flex flex-wrap items-baseline gap-x-2">
                <span dir="auto" className="min-w-0 text-base font-medium wrap-anywhere">{token.name}</span>
                <span className={state === 'Active' ? 'text-muted-foreground' : 'font-medium text-destructive'}>{state}</span>
              </div>
              <span>{journalAccessWords(token)}</span>
              <span className="text-muted-foreground">{permissionsWords(token.permissions)}</span>
              {token.jobTools && <span className="text-muted-foreground">Can use the job tools of the Owner</span>}
              <span className="text-muted-foreground">
                Created on {dayOf(token.createdAt)}. {expiryWords(token, state)}.{' '}
                {token.lastUsedAt ? `Last used on ${timeOf(token.lastUsedAt)}.` : 'Not used yet.'}
              </span>
              {token.revokedAt && <span className="text-muted-foreground">Revoked on {dayOf(token.revokedAt)}.</span>}
            </div>
            {token.allowedActions.includes('Revoke') && <RevokeToken token={token} headingRef={headingRef} />}
          </li>
        )
      })}
    </ul>
  )
}

function RevokeToken({ token, headingRef }: { token: PersonalAccessTokenDto; headingRef: RefObject<HTMLHeadingElement | null> }) {
  const revoke = useRevokePersonalAccessToken()

  const handleRevoke = () =>
    revoke.mutate(token.id, {
      onSuccess: () => Notification.success('Token revoked'),
      onSettled: () => headingRef.current?.focus(),
    })

  return (
    <ResponsiveDialogTrigger>
      <Button variant="outline" className="shrink-0" aria-label={`Revoke ${token.name}`} isPending={revoke.isPending}>
        Revoke
      </Button>
      <ResponsiveDialog>
        <ResponsiveDialogHeader>
          <ResponsiveDialogTitle>Revoke {token.name}?</ResponsiveDialogTitle>
          <ResponsiveDialogDescription>
            Recall refuses each request with this token from now on. You cannot undo a revoke.
          </ResponsiveDialogDescription>
        </ResponsiveDialogHeader>
        <ResponsiveDialogFooter>
          <ResponsiveDialogClose>Keep token</ResponsiveDialogClose>
          <ResponsiveDialogClose variant="destructive" onPress={handleRevoke}>
            Revoke token
          </ResponsiveDialogClose>
        </ResponsiveDialogFooter>
      </ResponsiveDialog>
    </ResponsiveDialogTrigger>
  )
}
