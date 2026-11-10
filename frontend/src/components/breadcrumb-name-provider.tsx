import { useState, type ReactNode } from 'react'

import { NameContext, SetNameContext } from './breadcrumb-name'

export function BreadcrumbNameProvider({ children }: { children: ReactNode }) {
  const [name, setName] = useState<string>()
  return (
    <SetNameContext value={setName}>
      <NameContext value={name}>{children}</NameContext>
    </SetNameContext>
  )
}
