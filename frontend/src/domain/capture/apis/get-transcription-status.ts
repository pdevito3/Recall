import { useQuery } from '@tanstack/react-query'

import { getTranscriptionStatus } from '@/api/generated/recordings/clients/get-transcription-status'

import { CaptureKeys } from './capture.keys'

const statusCheckMs = 60_000

export const useTranscriptionStatus = () =>
  useQuery({
    queryKey: CaptureKeys.status(),
    queryFn: ({ signal }) => getTranscriptionStatus({ signal, options: { suppressErrorToast: true } }),
    refetchInterval: statusCheckMs,
    refetchOnWindowFocus: true,
    retry: false,
  })
