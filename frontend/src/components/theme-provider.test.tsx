import { use } from 'react'
import { afterEach, expect, test } from 'vitest'
import { render } from 'vitest-browser-react'

import { ThemeProviderContext } from './theme-context'
import { ThemeProvider } from './theme-provider'

function ChooseDark() {
  const { setTheme } = use(ThemeProviderContext)
  return <button onClick={() => setTheme('dark')}>Dark</button>
}

afterEach(() => {
  localStorage.removeItem('ui-theme')
  document.cookie = 'ui-theme=; Path=/; Max-Age=0'
  document.documentElement.classList.remove('dark')
})

test('a choice of a theme goes in a cookie, so the sign-in pages of the Server can follow it', async () => {
  const screen = await render(
    <ThemeProvider>
      <ChooseDark />
    </ThemeProvider>,
  )

  await screen.getByRole('button', { name: 'Dark' }).click()

  expect(document.cookie).toContain('ui-theme=dark')
})
