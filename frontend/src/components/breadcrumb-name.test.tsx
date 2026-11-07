import { useState } from 'react'
import { expect, test } from 'vitest'
import { render } from 'vitest-browser-react'

import { useBreadcrumbName, useCurrentBreadcrumbName } from './breadcrumb-name'
import { BreadcrumbNameProvider } from './breadcrumb-name-provider'

let changeHeading: (heading: string) => void = () => undefined

function Page() {
  const [heading, setHeading] = useState('11:45 PM')
  changeHeading = setHeading
  useBreadcrumbName(heading)
  return <h1>{heading}</h1>
}

const Crumb = () => <nav>{useCurrentBreadcrumbName()}</nav>

test('the breadcrumb and the heading change together, so no read sees two names', async () => {
  const screen = await render(
    <BreadcrumbNameProvider>
      <Crumb />
      <Page />
    </BreadcrumbNameProvider>,
  )
  await expect.element(screen.getByRole('navigation')).toHaveTextContent('11:45 PM')
  const heading = screen.getByRole('heading').element()
  const crumb = screen.getByRole('navigation').element()
  const seen: (string | null)[] = []
  const watch = new MutationObserver(() => seen.push(crumb.textContent))
  watch.observe(heading, { characterData: true, subtree: true, childList: true })

  setTimeout(() => changeHeading('1:45 AM'))

  await expect.element(screen.getByRole('heading')).toHaveTextContent('1:45 AM')
  watch.disconnect()
  expect(seen).toEqual(['1:45 AM'])
})
