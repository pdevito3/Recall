import { createFileRoute } from '@tanstack/react-router'

import { LoginProvidersPage } from '@/domain/login-providers/components/login-providers-page'

export const Route = createFileRoute('/login-providers')({
  component: LoginProvidersPage,
  staticData: { breadcrumb: 'Login providers' },
})
