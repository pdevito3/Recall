import type { ReactNode } from 'react'

export function Page({ children }: { children: ReactNode }) {
  return <div className="mx-auto flex w-full max-w-3xl flex-col gap-6 py-4 md:py-8">{children}</div>
}
