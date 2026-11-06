import { createContext, use, useEffect } from 'react'

export const NameContext = createContext<string | undefined>(undefined)
export const SetNameContext = createContext<(name: string | undefined) => void>(() => undefined)

export const useCurrentBreadcrumbName = () => use(NameContext)

/** A detail page gives the name of its record here. The breadcrumb shows that name in place of the name of the route. */
export function useBreadcrumbName(name: string | undefined) {
  const setName = use(SetNameContext)
  useEffect(() => {
    setName(name)
    return () => setName(undefined)
  }, [name, setName])
}
