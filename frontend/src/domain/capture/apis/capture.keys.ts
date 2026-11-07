export const CaptureKeys = {
  all: ['capture'] as const,
  status: () => [...CaptureKeys.all, 'status'] as const,
}
