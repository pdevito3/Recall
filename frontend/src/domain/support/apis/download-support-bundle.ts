import { useMutation } from '@tanstack/react-query'

import { getSupportBundle } from '@/api/generated/support/clients/get-support-bundle'

function saveToDisk(zip: Blob) {
  const url = URL.createObjectURL(zip)
  const link = document.createElement('a')
  link.href = url
  link.download = 'recall-support.zip'
  link.click()
  URL.revokeObjectURL(url)
}

/** A session cookie needs the X-CSRF header, so a plain link to the Server cannot start this download. */
export function useDownloadSupportBundle() {
  return useMutation({
    mutationFn: async () => saveToDisk(await getSupportBundle({ responseType: 'blob' })),
  })
}
