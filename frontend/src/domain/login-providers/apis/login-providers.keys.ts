export const LoginProviderKeys = {
  all: ['login-providers'] as const,
  lists: () => [...LoginProviderKeys.all, 'list'] as const,
}
