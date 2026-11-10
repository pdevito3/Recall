export type DraftChannel = 'raw' | 'cleaned'

export const SessionKeys = {
  all: ['sessions'] as const,
  timeline: () => [...SessionKeys.all, 'timeline'] as const,
  details: () => [...SessionKeys.all, 'detail'] as const,
  detail: (id: string) => [...SessionKeys.details(), id] as const,
  revisions: (id: string, channel: DraftChannel) => [...SessionKeys.details(), id, 'revisions', channel] as const,
  revision: (id: string, channel: DraftChannel, number: number) =>
    [...SessionKeys.details(), id, 'revisions', channel, number] as const,
}
