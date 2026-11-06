import { useMutation } from '@tanstack/react-query'

import { getSupportBundle } from '@/api/generated/support/clients/get-support-bundle'

const urlLifetimeMs = 40_000

function saveToDisk(zip: Blob) {
  const url = URL.createObjectURL(zip)
  const link = document.createElement('a')
  link.href = url
  link.download = 'recall-support.zip'
  link.click()
  // WebKit can fail a download when its URL goes at once
  setTimeout(() => URL.revokeObjectURL(url), urlLifetimeMs)
}

export function useDownloadSupportBundle() {
  return useMutation({
    mutationFn: async () => saveToDisk(await getSupportBundle({ responseType: 'blob' })),
  })
}
