export const PersonKeys = {
  all: ['people'] as const,
  directory: () => [...PersonKeys.all, 'directory'] as const,
}
