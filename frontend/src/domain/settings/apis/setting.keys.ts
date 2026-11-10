export const SettingKeys = {
  all: ['settings'] as const,
  mine: () => [...SettingKeys.all, 'mine'] as const,
}
