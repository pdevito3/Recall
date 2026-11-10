export const CaptureKeys = {
  all: ['capture'] as const,
  status: () => [...CaptureKeys.all, 'status'] as const,
  recordings: (sessionId: string) => [...CaptureKeys.all, 'recordings', sessionId] as const,
  recording: (recordingId: string) => [...CaptureKeys.all, 'recording', recordingId] as const,
}
