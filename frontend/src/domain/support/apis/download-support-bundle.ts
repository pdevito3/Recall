import { useMutation } from '@tanstack/react-query'

import { getSupportBundle } from '@/api/generated/support/clients/get-support-bundle'

const urlLifetimeMs = 40_000

const fileNameIfTheServerGivesNone = 'recall-support.zip'

function fileNameIn(contentDisposition: unknown) {
  return typeof contentDisposition === 'string' ? /filename="?([^";]+)"?/.exec(contentDisposition)?.[1] : undefined
}

function saveToDisk(zip: Blob, fileName: string) {
  const url = URL.createObjectURL(zip)
  const link = document.createElement('a')
  link.href = url
  link.download = fileName
  link.click()
  // WebKit can fail a download when its URL goes at once
  setTimeout(() => URL.revokeObjectURL(url), urlLifetimeMs)
}

export function useDownloadSupportBundle() {
  return useMutation({
    mutationFn: async () => {
      // A client with no throw gives the headers, and its own check of the status keeps the toast of a refusal
      const { data, response } = await getSupportBundle({
        responseType: 'blob',
        throwOnError: false,
        validateStatus: (status) => status === 200,
      })
      if (data) saveToDisk(data, fileNameIn(response.headers['content-disposition']) ?? fileNameIfTheServerGivesNone)
    },
  })
}
