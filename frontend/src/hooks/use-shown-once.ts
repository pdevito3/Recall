import type { UseMutationResult } from '@tanstack/react-query'
import { useBlocker } from '@tanstack/react-router'
import { useRef, useState } from 'react'

import { isDeparting } from '@/domain/auth/apis/leave-the-app'

export interface Leaving {
  isAsking: boolean
  onStay: () => void
  onLeave: () => void
}

export function useShownOnce<TData, TVariables>(mutation: UseMutationResult<TData, unknown, TVariables, unknown>) {
  const [isOpen, setIsOpen] = useState(false)
  const isLocked = mutation.isPending || mutation.data !== undefined
  const hasSent = useRef(false)
  const isAsking = useRef(false)
  const blocker = useBlocker({
    shouldBlockFn: () => {
      // A move while the question shows lets the first one through, to where the browser now is. After a Forward, that is this page.
      if (isAsking.current) {
        handleLeave()
        return false
      }
      if (!hasSent.current) return false
      isAsking.current = true
      return true
    },
    enableBeforeUnload: () => hasSent.current && !isDeparting(),
    withResolver: true,
  })
  const handleLeave = () => {
    isAsking.current = false
    blocker.proceed?.()
  }
  const handleStay = () => {
    isAsking.current = false
    blocker.reset?.()
  }

  const onOpenChange = (open: boolean) => {
    if (!open && hasSent.current && mutation.data === undefined) return
    setIsOpen(open)
    if (open) return
    hasSent.current = false
    mutation.reset()
  }

  const send = (variables: TVariables, onError: (error: unknown) => void) => {
    if (hasSent.current) return
    hasSent.current = true
    mutation.mutate(variables, {
      onError: (error) => {
        hasSent.current = false
        onError(error)
      },
    })
  }

  const leaving: Leaving = { isAsking: blocker.status === 'blocked', onStay: handleStay, onLeave: handleLeave }
  return { isOpen, onOpenChange, send, isLocked, leaving }
}
