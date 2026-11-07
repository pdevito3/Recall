import type { MockRoute } from './mock-route'

const statusPath = '/api/v1/recordings/status'

export const recordingsApi = {
  jobsRun(): MockRoute {
    return { method: 'get', path: statusPath, status: 200, body: { jobsStopped: false, jobsStoppedText: null, model: 'Ready', modelText: null } }
  },

  jobsStopped(text: string): MockRoute {
    return { method: 'get', path: statusPath, status: 200, body: { jobsStopped: true, jobsStoppedText: text, model: 'Ready', modelText: null } }
  },
}
