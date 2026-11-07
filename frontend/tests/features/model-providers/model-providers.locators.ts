import type { Page } from '@playwright/test'

export const modelProviders = (page: Page) => ({
  heading: page.getByRole('heading', { level: 1, name: 'Model providers' }),
  name: page.getByLabel('Name'),
  baseUrl: page.getByLabel('Base URL'),
  apiKey: page.getByLabel('API key', { exact: true }),
  connect: page.getByRole('button', { name: 'Connect' }),
  test: page.getByRole('button', { name: 'Test connection' }),
  connected: page.getByRole('status').filter({ hasText: 'Connected.' }),
  connectFailed: page.getByRole('alert').filter({ hasText: 'The connection failed.' }),
  modelSelect: (provider: string) => page.getByRole('button', { name: new RegExp(`Model of ${provider}`) }),
  modelOption: (name: string) => page.getByRole('option', { name }),
  noAdminNote: page.getByText('An Admin connects the model provider of this install.'),
  keyRingWarning: page.getByRole('alert').filter({ hasText: 'The key ring changed' }),
  providerHeading: (name: string) => page.getByRole('heading', { level: 3, name }),
  fieldError: (text: string) => page.getByText(text),
  text: (words: string) => page.getByText(words),
  pageOverflow: () => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth),
  /** Each part of a shown address, between two slashes, that the page breaks over two lines. */
  addressPartsThatBreak: (address: string) =>
    page.evaluate((shown) => {
      const element = [...document.querySelectorAll('dd')].find((dd) => dd.textContent === shown)
      if (!element) throw new Error(`no address ${shown}`)
      const range = document.createRange()
      return [...element.childNodes]
        .filter((node) => node.nodeType === Node.TEXT_NODE)
        .filter((node) => {
          range.selectNodeContents(node)
          return range.getClientRects().length > 1
        })
        .map((node) => node.textContent)
    }, address),
  runCleanup: page.getByRole('button', { name: /^Run Cleanup/ }),
  stopCleanup: page.getByRole('button', { name: 'Stop Cleanup' }),
  makeActive: page.getByRole('button', { name: 'Make active' }),
  newUrl: page.getByLabel('New URL'),
  newApiKey: page.getByLabel('New API key'),
  saveUrl: page.getByRole('button', { name: 'Save URL' }),
  saveKey: page.getByRole('button', { name: 'Save key' }),
  noProviderNote: page.getByText('An Admin must connect a model provider, pick a model, and make it active first.'),
  controls: page.locator('main').getByRole('button').or(page.locator('main input:not([type=hidden])')),
  wordsGoTo: (name: string) => page.getByText(`A Cleanup sends your words to ${name}.`),
  wordsGoToAnyProvider: page.getByText('A Cleanup sends your words to'),
  tagPerson: (label: string) => page.getByRole('button', { name: `Tag ${label}`, exact: true }),
  doNotTagPerson: (label: string) => page.getByRole('button', { name: `Do not tag ${label}`, exact: true }),
  acceptSuggestion: (value: string) => page.getByRole('button', { name: `Accept ${value}`, exact: true }),
  rejectSuggestion: (value: string) => page.getByRole('button', { name: `Reject ${value}`, exact: true }),
  cleanupRunning: page.getByRole('status').filter({ hasText: 'You can close this page' }),
  toasts: page.getByRole('alertdialog'),
  sessionLink: (preview: string) => page.getByRole('link', { name: preview }),
  cleanupStopped: page.getByRole('status').filter({ hasText: 'You stopped the last Cleanup.' }),
  sessionRow: (preview: string) => page.getByRole('listitem').filter({ hasText: preview }),
  cleanupRefused: (text: string) => page.getByRole('alert').filter({ hasText: text }),
  cleanupFailure: (text: string) => page.getByRole('alert').filter({ hasText: text }),
  cleanedWords: page.locator('section', { has: page.getByRole('heading', { level: 3, name: 'Cleaned' }) }),
  rawWords: page.locator('section', { has: page.getByRole('heading', { level: 3, name: 'Raw' }) }),
})
