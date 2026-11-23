export const PersonKeys = {
  all: ['people'] as const,
  directory: () => [...PersonKeys.all, 'directory'] as const,
  details: () => [...PersonKeys.all, 'detail'] as const,
  detail: (id: string) => [...PersonKeys.details(), id] as const,
}
