import type { Page } from '@playwright/test'

/**
 * Waits until the app has the code of one page. An `import()` of the chunk that the app asked for loads nothing again, and
 * it resolves when each module that the chunk imports is in.
 *
 * Call it before the navigation that loads the app, so that it sees the answer of the chunk.
 */
export function theCodeOfAPageLoads(page: Page, chunk: RegExp): Promise<void> {
  return page.waitForResponse(chunk).then((answer) => page.evaluate((url) => import(url).then(() => undefined), answer.url()))
}
