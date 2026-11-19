import type { ReactNode } from 'react'

interface EmptyStateProps {
  art: ReactNode
  title: string
  description?: string
  action?: ReactNode
}

export function EmptyState({ art, title, description, action }: EmptyStateProps) {
  return (
    <div className="flex flex-col items-center px-4 py-4 text-center">
      {art}
      <p className="mt-4 font-semibold">{title}</p>
      {description && <p className="mt-1 max-w-sm text-sm text-muted-foreground">{description}</p>}
      {action && <div className="mt-4">{action}</div>}
    </div>
  )
}
