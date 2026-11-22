export const ExternalLoginKeys = {
  all: ['external-logins'] as const,
  lists: () => [...ExternalLoginKeys.all, 'list'] as const,
  mine: () => [...ExternalLoginKeys.lists(), 'mine'] as const,
  ofUser: (userId: string) => [...ExternalLoginKeys.lists(), 'user', userId] as const,
  providers: () => [...ExternalLoginKeys.all, 'providers'] as const,
}
