import { useMutation } from '@tanstack/react-query'

import { deleteSession } from '@/api/generated/sessions/clients/delete-session'

export function useDeleteSession() {
  return useMutation({ mutationFn: (id: string) => deleteSession({ path: { id } }) })
}
