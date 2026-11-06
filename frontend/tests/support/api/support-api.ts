import { aProblem } from '../builders/problem-details-builder'
import type { MockRoute } from './mock-route'

export const supportPaths = {
  bundle: '/api/v1/support-bundle',
}

export const supportApi = {
  bundleIsReady(fileName: string): MockRoute {
    return {
      method: 'get',
      path: supportPaths.bundle,
      status: 200,
      body: {},
      headers: {
        'Content-Type': 'application/zip',
        'Content-Disposition': `attachment; filename=${fileName}; filename*=UTF-8''${fileName}`,
      },
    }
  },

  bundleRefuses(detail: string): MockRoute {
    return { method: 'get', path: supportPaths.bundle, status: 403, body: { ...aProblem().saying(detail).build(), status: 403 } }
  },
}
