export const CorrectionKeys = {
  all: ['corrections'] as const,
  directory: () => [...CorrectionKeys.all, 'directory'] as const,
  details: () => [...CorrectionKeys.all, 'detail'] as const,
  detail: (id: string) => [...CorrectionKeys.details(), id] as const,
}
