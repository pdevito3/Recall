import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { expect, onTestFinished, test } from 'vitest'
import { page } from 'vitest/browser'
import { render } from 'vitest-browser-react'

import type { ModelProviderDto } from '@/api/generated/models/types/model-provider-dto'

import { ModelProviderCard } from './model-provider-card'

const speechBox: ModelProviderDto = {
  id: 'mp_2',
  kind: 'OpenAiCompatible',
  name: 'Office speech box',
  baseUrl: 'http://192.168.1.20:8000/v1',
  model: 'whisper-large-v3-turbo',
  isActive: false,
  keyState: 'Readable',
  version: 1,
}

test('at 320 px with a font of 24 px, each control of an OpenAI-compatible card stays inside the card, with no side scroll', async () => {
  // Arrange
  await page.viewport(320, 640)
  document.documentElement.style.fontSize = '24px'
  onTestFinished(() => {
    document.documentElement.style.fontSize = ''
  })

  // Act
  const screen = await render(
    // The padding of the content of the layout.
    <div className="p-4">
      <QueryClientProvider client={new QueryClient()}>
        <ModelProviderCard provider={speechBox} />
      </QueryClientProvider>
    </div>,
  )

  // Assert
  await expect.element(screen.getByRole('button', { name: 'Delete' })).toBeVisible()
  const card = screen.getByRole('heading', { name: 'Office speech box' }).element().closest('[data-slot=card]')
  const edge = card?.getBoundingClientRect().right ?? 0
  expect(edge).toBeGreaterThan(0)
  expect(edge).toBeLessThanOrEqual(320)
  for (const control of screen.container.querySelectorAll('button, input, [role=combobox]')) {
    expect(control.getBoundingClientRect().right, control.textContent || control.id).toBeLessThanOrEqual(edge)
  }
  expect(screen.getByText('OpenAI-compatible').element().getBoundingClientRect().right).toBeLessThanOrEqual(edge)
  expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(320)
})
