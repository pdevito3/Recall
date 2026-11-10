import type { MockRoute } from './mock-route'

export const fileStoreApi = {
  takesWrites(): MockRoute {
    return { method: 'get', path: '/api/v1/file-store/status', status: 200, body: { canWrite: true } }
  },

  refusesWrites(): MockRoute {
    return { method: 'get', path: '/api/v1/file-store/status', status: 200, body: { canWrite: false } }
  },
}
