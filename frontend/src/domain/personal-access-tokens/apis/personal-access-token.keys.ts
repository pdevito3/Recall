export const PersonalAccessTokenKeys = {
  all: ['personal-access-tokens'] as const,
  lists: () => [...PersonalAccessTokenKeys.all, 'list'] as const,
  mine: () => [...PersonalAccessTokenKeys.lists(), 'mine'] as const,
  ofUser: (userId: string) => [...PersonalAccessTokenKeys.lists(), 'user', userId] as const,
}
