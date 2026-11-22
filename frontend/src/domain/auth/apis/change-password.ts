import { useMutation } from '@tanstack/react-query'

import { apiClient } from '@/lib/api-client'

export const changePasswordPath = '/auth/password/change'

export interface PasswordChange {
  currentPassword: string
  newPassword: string
}

/**
 * The route is an /auth route, not in the OpenAPI document, so it takes a form body like the other password forms. The
 * Server keeps this session and ends each other session and token (PWD-17), so nothing in the cache changes.
 */
export function useChangePassword() {
  return useMutation({
    gcTime: 0,
    mutationFn: ({ currentPassword, newPassword }: PasswordChange) =>
      apiClient.post(changePasswordPath, new URLSearchParams({ currentPassword, newPassword }), { suppressErrorToast: true }),
  })
}
