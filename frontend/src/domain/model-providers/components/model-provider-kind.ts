import type { ModelProviderDtoKindEnumKey } from '@/api/generated/models/types/model-provider-dto'

export const KIND_LABELS: Record<ModelProviderDtoKindEnumKey, string> = {
  Anthropic: 'Anthropic',
  OpenAiCompatible: 'OpenAI-compatible',
}

export const MODEL_PROVIDER_KINDS = Object.keys(KIND_LABELS) as ModelProviderDtoKindEnumKey[]

export function isModelProviderKind(key: unknown): key is ModelProviderDtoKindEnumKey {
  return typeof key === 'string' && key in KIND_LABELS
}
