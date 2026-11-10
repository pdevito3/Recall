import { useQuery } from '@tanstack/react-query'

import { getUserByIdentifier } from '@/api/generated/users/clients/get-user-by-identifier'
import type { UserDto } from '@/api/generated/models/types/user-dto'
import { authUserQuery } from '@/domain/auth/apis/get-user'
import { isProblemDetails, isProblemDetailsError, ProblemDetailsError } from '@/lib/problem-details'

import { MembershipKeys } from './membership.keys'

/** The state of the signed-in person's user record on the Server. A user record means a tenant. */
export type Membership =
  | { status: 'loading' }
  | { status: 'error'; message: string; retry?: () => void }
  | { status: 'member'; user: UserDto }

async function findUser(identifier: string, signal: AbortSignal): Promise<UserDto> {
  // The pages show the failure, so a toast would say it twice.
  const result = await getUserByIdentifier({
    path: { identifier },
    throwOnError: false,
    signal,
    options: { suppressErrorToast: true },
  })
  if (result.status === 200) return result.data
  throw isProblemDetails(result.error)
    ? new ProblemDetailsError(result.error)
    : new Error(`The membership check failed with status ${result.status}.`)
}

/** A refusal of the Server, such as a 403 for a deleted user whose tenant is still cached, does not change on an immediate retry. */
const isRefusal = (error: unknown): error is ProblemDetailsError =>
  isProblemDetailsError(error) && (error.status ?? 500) < 500

const CHECK_FAILED = 'We could not check your account.'

export function useMembership(): Membership {
  const session = useQuery(authUserQuery)
  const identifier = session.data?.sub
  const user = useQuery({
    queryKey: MembershipKeys.of(identifier ?? ''),
    queryFn: ({ signal }) => findUser(identifier!, signal),
    enabled: !!identifier,
    retry: (failureCount, error) => !isRefusal(error) && failureCount < 1,
  })

  // A failed refetch keeps the data of the last answer, and that answer is still good to show.
  if (session.data === undefined) {
    return session.isError
      ? { status: 'error', message: CHECK_FAILED, retry: () => void session.refetch() }
      : { status: 'loading' }
  }
  if (!identifier) return { status: 'error', message: CHECK_FAILED }
  if (user.data === undefined) {
    if (!user.isError) return { status: 'loading' }
    return isRefusal(user.error)
      ? { status: 'error', message: user.error.displayMessage }
      : { status: 'error', message: CHECK_FAILED, retry: () => void user.refetch() }
  }
  return { status: 'member', user: user.data }
}
