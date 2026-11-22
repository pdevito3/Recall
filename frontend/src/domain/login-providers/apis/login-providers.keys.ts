export const LoginProviderKeys = {
  all: ['login-providers'] as const,
  lists: () => [...LoginProviderKeys.all, 'list'] as const,
  settings: () => [...LoginProviderKeys.all, 'settings'] as const,
}
