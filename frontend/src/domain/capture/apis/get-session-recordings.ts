import { useQuery } from '@tanstack/react-query'

import type { RecordingDto } from '@/api/generated/models/types/recording-dto'
import { getSessionRecordings } from '@/api/generated/recordings/clients/get-session-recordings'

import { CaptureKeys } from './capture.keys'

const pollWhileWorkingMs = 5_000

const hasEnded = ({ status }: RecordingDto) => status === 'Transcribed' || status === 'Failed'

export function useSessionRecordings(sessionId: string) {
  return useQuery({
    queryKey: CaptureKeys.recordings(sessionId),
    queryFn: ({ signal }) => getSessionRecordings({ path: { id: sessionId }, signal, options: { suppressErrorToast: true } }),
    // The server makes the text after the upload, and the card shows each step until the work ends.
    refetchInterval: (query) => (query.state.data?.every(hasEnded) === false ? pollWhileWorkingMs : false),
  })
}
