import type { SummaryDto } from '@/api/generated/models/types/summary-dto'
import type { TranscriptionEngineDto } from '@/api/generated/models/types/transcription-engine-dto'

import type { MockRoute } from './mock-route'

/** The Summaries page opens a period with a POST that starts its run, and the answer is the Summary as it is now. */
export const summariesApi = {
  dayOpens(date: string, changes: Partial<SummaryDto> = {}): MockRoute {
    const summary: SummaryDto = { period: 'Day', periodDate: date, periodLastDate: date, status: 'Missing', failure: 'None', content: '', sourceCount: 0, ...changes }
    return { method: 'post', path: `/api/v1/summaries/Day/${date}/generate`, status: 200, body: summary }
  },
}

export const transcriptionEngineApi = {
  isLocal(): MockRoute {
    const engine: TranscriptionEngineDto = { engine: 'Local', reason: 'NoProviderChosen', text: 'Recall makes the text of each Recording on this server.' }
    return { method: 'get', path: '/api/v1/transcription-engine', status: 200, body: engine }
  },
}
