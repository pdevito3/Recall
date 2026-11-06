import type { ReactNode } from 'react'

// Each page uses this box, so that the left edge of the content is the same on each page.
export function Page({ children }: { children: ReactNode }) {
  return <div className="mx-auto flex w-full max-w-3xl flex-col gap-6 py-4 md:py-8">{children}</div>
}
