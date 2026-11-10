import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import type { RecordingDto } from '@/api/generated/models/types/recording-dto'
import { getRecording } from '@/api/generated/recordings/clients/get-recording'
import { retryRecording } from '@/api/generated/recordings/clients/retry-recording'
import { SessionKeys } from '@/domain/sessions/apis/session.keys'
import { refreshTimelineHead } from '@/domain/sessions/apis/timeline-cache'

import { CaptureKeys } from './capture.keys'

const pollWhileWorkingMs = 3_000

const hasEnded = (recording: RecordingDto | undefined) => recording?.status === 'Transcribed' || recording?.status === 'Failed'

/** The Server row of a recording that this browser sent. It reads again until the text is done or the work failed. */
export function useRecording(recordingId: string | null) {
  const queryClient = useQueryClient()
  return useQuery({
    queryKey: CaptureKeys.recording(recordingId ?? ''),
    queryFn: async ({ signal }) => {
      const recording = await getRecording({ path: { id: recordingId! }, signal, options: { suppressErrorToast: true } })
      // The text joins the entry as the work goes, so the timeline and the entry read it when it ends.
      if (recording.status === 'Transcribed') {
        void refreshTimelineHead(queryClient)
        void queryClient.invalidateQueries({ queryKey: SessionKeys.detail(recording.sessionId) })
      }
      return recording
    },
    enabled: recordingId !== null,
    refetchInterval: (query) => (hasEnded(query.state.data) ? false : pollWhileWorkingMs),
  })
}

export function useRetryRecording() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (recordingId: string) => retryRecording({ path: { id: recordingId }, options: { suppressErrorToast: true } }),
    onSuccess: (recording) => queryClient.setQueryData(CaptureKeys.recording(recording.id), recording),
  })
}
