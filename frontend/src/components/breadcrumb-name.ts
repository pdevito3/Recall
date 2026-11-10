import { createContext, use, useLayoutEffect } from 'react'

export const NameContext = createContext<string | undefined>(undefined)
export const SetNameContext = createContext<(name: string | undefined) => void>(() => undefined)

export const useCurrentBreadcrumbName = () => use(NameContext)

export function useBreadcrumbName(name: string | undefined) {
  const setName = use(SetNameContext)
  // Before the paint, so the breadcrumb and the heading never show two names.
  useLayoutEffect(() => {
    setName(name)
    return () => setName(undefined)
  }, [name, setName])
}
