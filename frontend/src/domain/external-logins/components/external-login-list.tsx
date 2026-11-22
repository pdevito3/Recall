import type { UseQueryResult } from '@tanstack/react-query'
import type { RefObject } from 'react'

import type { ExternalLoginDto } from '@/api/generated/models/types/external-login-dto'
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
import { isProblemDetailsError } from '@/lib/problem-details'
import { browserTimeZone, formatInstant } from '@/lib/zoned-time'

import { useRemoveExternalLogin } from '../apis/external-logins'

interface ExternalLoginListProps {
  links: UseQueryResult<ExternalLoginDto[]>
  label: string
  emptyText: string
  headingRef: RefObject<HTMLHeadingElement | null>
  /** The user whose links these are, or none for the links of the caller. */
  userId?: string
}

const timeOf = (instant: string) =>
  formatInstant(new Date(instant), browserTimeZone(), { year: 'numeric', month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' })

const nameOfProvider = (link: ExternalLoginDto) => link.providerName ?? 'A provider that is not on the login page'

export function ExternalLoginList({ links, label, emptyText, headingRef, userId }: ExternalLoginListProps) {
  if (links.isPending) {
    return <p role="status" aria-label={`Loading ${label}`} className="text-muted-foreground">Loading the sign-in methods</p>
  }
  if (links.isError) {
    return <QueryError title="The sign-in methods did not load." error={links.error} onRetry={() => void links.refetch()} />
  }
  if (links.data.length === 0) {
    return <p className="text-muted-foreground wrap-anywhere">{emptyText}</p>
  }

  return (
    <ul aria-label={label} className="flex flex-col divide-y rounded-lg border">
      {links.data.map((link) => (
        <ExternalLoginItem key={`${link.issuer} ${link.subject}`} link={link} headingRef={headingRef} userId={userId} />
      ))}
    </ul>
  )
}

function ExternalLoginItem({ link, headingRef, userId }: { link: ExternalLoginDto; headingRef: RefObject<HTMLHeadingElement | null>; userId?: string }) {
  const remove = useRemoveExternalLogin(userId)
  const provider = nameOfProvider(link)
  const name = link.displayLabel ? `${provider}, ${link.displayLabel}` : provider

  const handleRemove = () =>
    remove.mutate(
      { issuer: link.issuer, subject: link.subject },
      {
        onSuccess: () => Notification.success('Sign-in method removed'),
        onSettled: () => headingRef.current?.focus(),
      },
    )

  return (
    <li aria-label={name} className="flex flex-wrap items-start justify-between gap-x-3 gap-y-2 p-3">
      <div className="flex min-w-0 flex-1 basis-60 flex-col gap-1 text-sm">
        <span className="text-base font-medium wrap-anywhere">{provider}</span>
        {link.displayLabel && <span dir="auto" className="wrap-anywhere">{link.displayLabel}</span>}
        {!link.providerName && <span className="text-muted-foreground wrap-anywhere">{link.issuer}</span>}
        <span className="text-muted-foreground">Last sign-in on {timeOf(link.lastLoginAt)}.</span>
        {remove.isError && (
          <p role="alert" className="text-destructive wrap-anywhere">
            {isProblemDetailsError(remove.error) ? remove.error.displayMessage : 'Recall could not remove this sign-in method. Try again.'}
          </p>
        )}
      </div>
      <ResponsiveDialogTrigger>
        <Button variant="outline" className="shrink-0" aria-label={`Remove ${name}`} isPending={remove.isPending}>
          Remove
        </Button>
        <ResponsiveDialog>
          <ResponsiveDialogHeader>
            <ResponsiveDialogTitle>Remove {provider}?</ResponsiveDialogTitle>
            <ResponsiveDialogDescription>
              This account at {provider} cannot sign in to Recall after this. To use it again, link it again from settings.
            </ResponsiveDialogDescription>
          </ResponsiveDialogHeader>
          <ResponsiveDialogFooter>
            <ResponsiveDialogClose>Keep it</ResponsiveDialogClose>
            <ResponsiveDialogClose variant="destructive" onPress={handleRemove}>
              Remove
            </ResponsiveDialogClose>
          </ResponsiveDialogFooter>
        </ResponsiveDialog>
      </ResponsiveDialogTrigger>
    </li>
  )
}
