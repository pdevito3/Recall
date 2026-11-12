import { expect, test } from 'vitest'
import { commands } from 'vitest/browser'

import { apiClient } from '@/lib/api-client'

const path = '/api/v1/late-request'
let late: Promise<unknown> = Promise.resolve()

test('a test can end while its request waits', async () => {
  // Arrange
  await commands.holdRequests(path)

  // Act
  late = apiClient.get(path, { suppressErrorToast: true }).catch((error: unknown) => error)

  // Assert
  expect(await commands.takeUnansweredRequests()).toEqual([])
})

test('a request of the last test that comes late does not count as one of this test', async () => {
  // Act
  await commands.releaseRequests(path)
  await late

  // Assert
  expect(await commands.takeUnansweredRequests()).toEqual([])
})
