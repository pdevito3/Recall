const api = (page, method, path, body) => page.evaluate(async ({ method, path, body }) => {
  const me = await (await fetch('/auth/me', { headers: { 'X-CSRF': '1' } })).json()
  const response = await fetch(path, { method, headers: { 'X-CSRF': '1', 'X-Recall-User': me.sub, 'Content-Type': 'application/json' }, body: body ? JSON.stringify(body) : undefined })
  const text = await response.text()
  return { status: response.status, body: text ? JSON.parse(text) : null }
}, { method, path, body })

const addEntry = async (page, words) => {
  const created = await api(page, 'POST', '/api/v1/sessions', { clientSessionId: crypto.randomUUID(), startedAt: new Date().toISOString() })
  await api(page, 'PUT', `/api/v1/sessions/${created.body.id}/draft`, { text: words, baseRevision: 0, savedAt: new Date().toISOString() })
  return created.body.id
}

const saved = (page) => page.getByRole('status').filter({ hasText: /^Saved/ })
const editor = (page) => page.getByRole('textbox', { name: 'Your words' })

const typeAtEnd = async (page, box, words) => {
  await box.click()
  await page.keyboard.press('ControlOrMeta+End')
  await page.keyboard.type(words)
}

const smallTargets = (page) => page.evaluate(() => {
  const controls = document.querySelectorAll('a[href], button, input:not([type="hidden"], [type="checkbox"]), select, textarea, [role="button"], [role="switch"], [role="tab"], [role="combobox"], [role="option"], [role="menuitem"], label:has(input[type="checkbox"]), [contenteditable="true"]')
  return [...controls]
    .filter((control) => !control.closest('[aria-hidden="true"], [inert]'))
    .map((control) => ({ control, box: control.getBoundingClientRect() }))
    .filter(({ box }) => box.width > 1 && box.height > 1 && (box.width < 44 || box.height < 44))
    .map(({ control, box }) => `${control.getAttribute('aria-label') ?? control.textContent?.trim().slice(0, 30) ?? control.tagName} ${Math.round(box.width)}x${Math.round(box.height)}`)
})

const sideScroll = (page) => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)

const overflowingText = (page) => page.evaluate(() => [...document.querySelectorAll('main *')]
  .filter((element) => {
    const box = element.getBoundingClientRect()
    return box.width > 0 && box.right > document.documentElement.clientWidth + 1
  })
  .map((element) => element.tagName + ' ' + (element.textContent ?? '').slice(0, 30)))

const focusIsVisible = (page) => page.evaluate(() => {
  const focused = document.activeElement
  if (!focused || focused === document.body) return { name: 'body', visible: false }
  const shows = (element) => {
    const style = getComputedStyle(element)
    return style.boxShadow !== 'none' || (style.outlineStyle !== 'none' && parseFloat(style.outlineWidth) > 0)
  }
  const frames = [focused, focused.parentElement, focused.parentElement?.parentElement, focused.parentElement?.parentElement?.parentElement]
  return { name: focused.getAttribute('aria-label') ?? focused.textContent?.trim().slice(0, 30) ?? focused.tagName, visible: frames.some((frame) => frame && shows(frame)) }
})

export default async ({ page, context, step, capture, expect, login }) => {
  await login()

  await step('lane 1: the app opens on the journal, and a new entry starts at the first word and shows in the timeline after Done', async () => {
    await page.goto('/')
    const composer = page.getByRole('textbox', { name: 'New entry' })
    await expect(composer).toBeFocused()
    await page.keyboard.type('Morning pages about the walk to the market.')
    await expect(saved(page)).toBeVisible()
    await page.getByRole('button', { name: 'Done', exact: true }).click()
    await expect(page.getByRole('link').filter({ hasText: 'Morning pages about the walk' })).toBeVisible()
    await expect(composer).toBeEmpty()
  })
  await capture('r12-create')

  await step('lane 2: all words return after a reload that comes after the saved message', async () => {
    await page.goto('/')
    await page.getByRole('textbox', { name: 'New entry' }).click()
    await page.keyboard.type('Words that a reload must not lose.')
    await expect(saved(page)).toBeVisible()
    await page.reload()
    await expect(page.getByRole('textbox', { name: 'New entry' })).toContainText('Words that a reload must not lose.')
    await page.getByRole('button', { name: 'Done', exact: true }).click()
    await page.getByRole('link').filter({ hasText: 'Words that a reload must not lose.' }).click()
    await expect(editor(page)).toContainText('Words that a reload must not lose.')
    await typeAtEnd(page, editor(page), ' And more words on the entry page.')
    await expect(saved(page)).toBeVisible()
    await page.reload()
    await expect(editor(page)).toContainText('Words that a reload must not lose. And more words on the entry page.')
  })
  await capture('r12-autosave')

  const twoTabs = await step('lane 3: two tabs edit one entry, the latest save shows, and the other save is in the history', async () => {
    const id = await addEntry(page, 'Words at the start.')
    const other = await context.newPage()
    await page.goto(`/sessions/${id}`)
    await other.goto(`/sessions/${id}`)
    await expect(editor(page)).toContainText('Words at the start.')
    await expect(editor(other)).toContainText('Words at the start.')

    await typeAtEnd(other, editor(other), ' The first tab.')
    await expect(saved(other)).toBeVisible()
    await typeAtEnd(page, editor(page), ' The second tab.')
    await expect(saved(page)).toBeVisible()
    await expect(page.getByRole('alert').filter({ hasText: 'These words replaced a save that this page had not shown.' })).toBeVisible()
    await capture('r12-conflict-notice')

    await page.getByRole('button', { name: 'Show history', exact: true }).click()
    const versions = page.getByRole('button', { name: /^View version from/ })
    await expect(versions).toHaveCount(3)
    await page.reload()
    await expect(editor(page)).toContainText('Words at the start. The second tab.')
    const read = await api(page, 'GET', `/api/v1/sessions/${id}/raw-revisions`)
    expect(read.status).toBe(200)

    await other.bringToFront()
    await other.reload()
    await expect(editor(other)).toContainText('Words at the start. The second tab.')
    await other.close()
    await page.bringToFront()
    await page.getByRole('button', { name: 'Show history', exact: true }).click()
    const earlier = page.getByRole('button', { name: /^View version from/ }).nth(1)
    await earlier.click()
    await expect(page.getByRole('dialog').getByText('Words at the start. The first tab.')).toBeVisible()
    return id
  })
  await capture('r12-conflict', { fullPage: false })

  await step('lane 4: a restored version gives the same words', async () => {
    await page.getByRole('button', { name: 'Restore this version', exact: true }).click()
    await expect(page.getByRole('dialog')).toHaveCount(0)
    await expect(editor(page)).toHaveText('Words at the start. The first tab.')
    await expect(saved(page)).toBeVisible()
    await page.reload()
    await expect(editor(page)).toHaveText('Words at the start. The first tab.')
    const read = await api(page, 'GET', `/api/v1/sessions/${twoTabs}`)
    expect(read.body.rawText).toContain('The first tab.')
  })
  await capture('r12-revision')

  await step('lane 6: a delete asks first, and the entry is gone after it', async () => {
    await page.getByRole('button', { name: 'Delete', exact: true }).click()
    await expect(page.getByRole('button', { name: 'Delete entry', exact: true })).toBeVisible()
  })
  await capture('r12-delete', { fullPage: false })
  await step('lane 6: the delete removes the entry', async () => {
    await page.getByRole('button', { name: 'Delete entry', exact: true }).click()
    await expect(page.getByRole('textbox', { name: 'New entry' })).toBeVisible()
    await expect(page.getByRole('link').filter({ hasText: 'Words at the start' })).toHaveCount(0)
    expect((await api(page, 'GET', `/api/v1/sessions/${twoTabs}`)).status).toBe(404)
  })

  const noNetwork = (route) => route.abort('internetdisconnected')
  await step('lane 9: an edit with no network says that the save waits, and the save ends after the network returns', async () => {
    const id = await addEntry(page, 'Words before the tunnel.')
    await page.goto(`/sessions/${id}`)
    await expect(editor(page)).toContainText('Words before the tunnel.')
    // A sweep fetches each API reply from Node, where setOffline has no effect, so the route also stops the API.
    await context.route('**/api/**', noNetwork)
    await context.setOffline(true)
    await typeAtEnd(page, editor(page), ' Words in the tunnel.')
    await expect(page.getByRole('status').filter({ hasText: 'The save waits for a connection' })).toBeVisible({ timeout: 15_000 })
  })
  await capture('r12-offline')
  await step('lane 9: the save ends after the network returns', async () => {
    await context.setOffline(false)
    await context.unroute('**/api/**', noNetwork)
    await expect(saved(page)).toBeVisible({ timeout: 35_000 })
    await page.reload()
    await expect(editor(page)).toContainText('Words before the tunnel. Words in the tunnel.')
  })

  await step('lane 7: at 390 px no text overflows, and each target is at least 44 px', async () => {
    const id = await addEntry(page, 'An entry for the phone.')
    await page.setViewportSize({ width: 390, height: 844 })
    const pages = [['/', 'New entry'], [`/sessions/${id}`, 'Your words']]
    for (const [path, box] of pages) {
      await page.goto(path)
      await expect(page.getByRole('textbox', { name: box })).toBeVisible()
      expect(await sideScroll(page)).toBe(0)
      expect(await overflowingText(page)).toEqual([])
      expect(await smallTargets(page)).toEqual([])
    }
    await page.getByRole('button', { name: 'Edit tags', exact: true }).click()
    await page.getByRole('button', { name: 'Show history', exact: true }).click()
    await expect(page.getByRole('button', { name: /^View version from/ }).first()).toBeVisible()
    expect(await smallTargets(page)).toEqual([])
    for (const [path, name] of [['/people', 'People'], ['/settings', 'Settings'], ['/users', 'Users'], ['/about', 'About']]) {
      await page.goto(path)
      await expect(page.getByRole('main').getByRole('heading', { level: 1, name })).toBeVisible()
      expect(await sideScroll(page)).toBe(0)
      expect(await smallTargets(page)).toEqual([])
    }
    await page.goto(`/sessions/${id}`)
    await expect(editor(page)).toBeVisible()
  })
  await capture('r12-phone')

  await step('lane 8: each action of the journal works from the keyboard with a visible focus', async () => {
    await page.setViewportSize({ width: 1280, height: 800 })
    await page.goto('/')
    const composer = page.getByRole('textbox', { name: 'New entry' })
    await expect(composer).toBeFocused()
    await page.keyboard.type('An entry from the keyboard.')
    await expect(saved(page)).toBeVisible()

    const unseen = []
    const tabTo = async (target) => {
      for (let press = 0; press < 40; press += 1) {
        await page.keyboard.press('Tab')
        const focus = await focusIsVisible(page)
        if (!focus.visible) unseen.push(focus.name)
        if (await target.evaluate((element) => element === document.activeElement || element.contains(document.activeElement))) return
      }
      throw new Error('Tab did not reach the target.')
    }

    await tabTo(page.getByRole('button', { name: 'Done', exact: true }))
    await page.keyboard.press('Enter')
    await expect(composer).toBeFocused()
    const row = page.getByRole('link').filter({ hasText: 'An entry from the keyboard.' })
    await expect(row).toBeVisible()
    await tabTo(row)
    await page.keyboard.press('Enter')
    await expect(editor(page)).toBeVisible()

    await tabTo(page.getByRole('button', { name: 'Show history', exact: true }))
    await page.keyboard.press('Enter')
    await tabTo(page.getByRole('button', { name: 'Edit tags', exact: true }))
    await page.keyboard.press('Enter')
    await tabTo(page.getByRole('button', { name: 'Calm', exact: true }))
    await page.keyboard.press('Space')
    await expect(page.getByRole('region', { name: 'Tags' }).getByRole('status').filter({ hasText: /^Saved/ })).toBeVisible()
    expect(unseen).toEqual([])
  })
  await capture('r12-keyboard', { fullPage: false })
  await step('lane 8: the delete works from the keyboard', async () => {
    await page.keyboard.press('Shift+Tab')
    for (let press = 0; press < 60; press += 1) {
      if (await page.getByRole('button', { name: 'Delete', exact: true }).evaluate((element) => element === document.activeElement)) break
      await page.keyboard.press('Shift+Tab')
    }
    await page.keyboard.press('Enter')
    await expect(page.getByRole('button', { name: 'Keep entry', exact: true })).toBeVisible()
    await page.keyboard.press('Escape')
    await expect(page.getByRole('button', { name: 'Keep entry', exact: true })).toHaveCount(0)
    await expect(page.getByRole('button', { name: 'Delete', exact: true })).toBeFocused()
  })
}
