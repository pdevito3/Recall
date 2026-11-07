import type { SummaryPeriod } from '../types'

export interface SummaryParams {
  period: SummaryPeriod
  /** A date in the period, as yyyy-MM-dd. */
  date: string
}

export const SummaryKeys = {
  all: ['summaries'] as const,
  details: () => [...SummaryKeys.all, 'detail'] as const,
  detail: (params: SummaryParams) => [...SummaryKeys.details(), params] as const,
}
