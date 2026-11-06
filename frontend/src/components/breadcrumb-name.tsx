import { createContext, use, useEffect, useState, type ReactNode } from 'react'

const NameContext = createContext<string | undefined>(undefined)
const SetNameContext = createContext<(name: string | undefined) => void>(() => undefined)

export function BreadcrumbNameProvider({ children }: { children: ReactNode }) {
  const [name, setName] = useState<string>()
  return (
    <SetNameContext value={setName}>
      <NameContext value={name}>{children}</NameContext>
    </SetNameContext>
  )
}

export const useCurrentBreadcrumbName = () => use(NameContext)

/** A detail page gives the name of its record here. The breadcrumb shows that name in place of the name of the route. */
export function useBreadcrumbName(name: string | undefined) {
  const setName = use(SetNameContext)
  useEffect(() => {
    setName(name)
    return () => setName(undefined)
  }, [name, setName])
}
