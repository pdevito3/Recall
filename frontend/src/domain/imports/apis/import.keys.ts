export const ImportKeys = {
  all: ['imports'] as const,
  lists: () => [...ImportKeys.all, 'list'] as const,
  batch: (id: string) => [...ImportKeys.all, 'batch', id] as const,
  days: (id: string) => [...ImportKeys.all, 'batch', id, 'days'] as const,
  entriesOf: (id: string) => [...ImportKeys.all, 'batch', id, 'entries'] as const,
  entries: (id: string, from: string, to: string) => [...ImportKeys.all, 'batch', id, 'entries', from, to] as const,
}
