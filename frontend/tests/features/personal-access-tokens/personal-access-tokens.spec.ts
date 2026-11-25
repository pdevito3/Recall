import { membershipApi } from '@tests/support/api/membership-api'
import { authApi } from '@tests/support/api/auth-api'
import { personalAccessTokensApi, personalAccessTokensPaths } from '@tests/support/api/personal-access-tokens-api'
import { usersApi } from '@tests/support/api/users-api'
import { actorSubject, as } from '@tests/support/auth/actors'
import { browserZone, instantIn } from '@tests/support/clock'
import { aToken } from '@tests/support/builders/personal-access-token-builder'
import { aProblem } from '@tests/support/builders/problem-details-builder'
import { aUser } from '@tests/support/builders/user-builder'
import { expect, test } from '@tests/support/fixtures'
import { personalAccessTokensPage } from './personal-access-tokens.tasks'

const value = `rcl_pat_${'Q'.repeat(43)}`

const endOfDayIn = (date: string) => new Date(instantIn(`${date}T00:00:00`, browserZone()).getTime() - 1).toISOString()

test.describe('the personal access tokens of a person', () => {
  test.use({ actor: as.user('Ana Admin') })

  test('shows the value once with Copy, and a reload shows the token with no value', { tag: ['@PAT-1', '@PAT-20'] }, async ({ page, context, api }) => {
    await context.grantPermissions(['clipboard-read', 'clipboard-write'])
    const laptop = aToken().named('Laptop')
    api.mock(personalAccessTokensApi.mineAre([]), personalAccessTokensApi.creates(laptop.created(value)))
    const tokens = personalAccessTokensPage(page)
    await tokens.openAt(instantIn('2026-11-16T10:00:00', browserZone()))
    await tokens.openForm()

    await expect(tokens.at.expiry).toContainText('In 90 days, on Feb 14, 2027')
    await tokens.at.nameField.fill('Laptop')
    api.mock(personalAccessTokensApi.mineAre([laptop]))
    await tokens.create()

    expect(api.last('post', personalAccessTokensPaths.mine)?.body).toEqual({
      name: 'Laptop',
      permissions: [],
      readJournal: false,
      writeJournal: false,
      expiresAt: endOfDayIn('2027-02-15'),
    })
    await expect(tokens.at.createdTitle).toBeVisible()
    await expect(tokens.at.value(value)).toBeVisible()
    await tokens.at.copy.click()
    await expect.poll(() => tokens.clipboardText()).toBe(value)
    await expect(tokens.at.toast('Token copied')).toBeVisible()
    await tokens.at.done.click()
    await expect(tokens.at.dialog).toHaveCount(0)
    await expect(tokens.at.row('Laptop')).toBeVisible()
    await expect(tokens.at.anyTokenValue).toHaveCount(0)
    expect(await tokens.storedText()).not.toContain(value)

    await tokens.reload()

    await expect(tokens.at.row('Laptop')).toBeVisible()
    await expect(tokens.at.anyTokenValue).toHaveCount(0)
  })

  test('with no clipboard API, as on plain HTTP, Copy puts the value on the clipboard', { tag: '@PAT-1' }, async ({ page, context, api }) => {
    await context.grantPermissions(['clipboard-read', 'clipboard-write'])
    await page.addInitScript(() => Object.defineProperty(navigator, 'clipboard', { value: undefined, configurable: true }))
    api.mock(personalAccessTokensApi.mineAre([]), personalAccessTokensApi.creates(aToken().named('Laptop').created(value)))
    const tokens = personalAccessTokensPage(page)
    const reader = await context.newPage()
    await reader.goto('/settings/personal-access-tokens')
    await reader.evaluate(() => navigator.clipboard.writeText('CONTROL'))
    await tokens.open()
    await tokens.openForm()
    await tokens.at.nameField.fill('Laptop')
    await tokens.create()

    await tokens.at.copy.click()

    await expect(tokens.at.toast('Token copied')).toBeVisible()
    await expect.poll(() => reader.evaluate(() => navigator.clipboard.readText())).toBe(value)
  })

  test('the created value closes only with Done', { tag: '@PAT-1' }, async ({ page, api }) => {
    api.mock(personalAccessTokensApi.mineAre([]), personalAccessTokensApi.creates(aToken().named('Laptop').created(value)))
    const tokens = personalAccessTokensPage(page)
    await tokens.open()
    await tokens.openForm()
    await tokens.at.nameField.fill('Laptop')
    await tokens.create()
    await expect(tokens.at.value(value)).toBeVisible()
    await expect(tokens.at.closeDialog).toHaveCount(0)

    await page.keyboard.press('Escape')
    await expect(tokens.at.value(value)).toBeVisible()
    await page.mouse.click(5, 5)
    await expect(tokens.at.value(value)).toBeVisible()

    await tokens.at.done.click()
    await expect(tokens.at.dialog).toHaveCount(0)
    await tokens.openForm()
    await expect(tokens.at.nameField).toHaveValue('')
    await expect(tokens.at.anyTokenValue).toHaveCount(0)
  })

  test('while a create runs, the dialog stays open and then shows the value', { tag: '@PAT-1' }, async ({ page, api }) => {
    let answer!: () => void
    const answered = new Promise<void>((resolve) => (answer = resolve))
    api.mock(personalAccessTokensApi.mineAre([]), { ...personalAccessTokensApi.creates(aToken().named('Laptop').created(value)), heldUntil: answered })
    const tokens = personalAccessTokensPage(page)
    await tokens.open()
    await tokens.openForm()
    await tokens.at.nameField.fill('Laptop')
    await tokens.at.create.click()
    await expect(tokens.at.cancel).toBeDisabled()
    await expect(tokens.at.closeDialog).toHaveCount(0)

    await page.keyboard.press('Escape')
    await page.mouse.click(5, 5)

    await expect(tokens.at.nameField).toBeVisible()
    answer()
    await expect(tokens.at.value(value)).toBeVisible()
  })

  test('before a create, Cancel, Escape, and a click outside close the form and send nothing', { tag: '@PAT-1' }, async ({ page, api }) => {
    api.mock(personalAccessTokensApi.mineAre([]))
    const tokens = personalAccessTokensPage(page)
    await tokens.open()

    await tokens.openForm()
    await tokens.at.cancel.click()
    await expect(tokens.at.dialog).toHaveCount(0)
    await tokens.openForm()
    await page.keyboard.press('Escape')
    await expect(tokens.at.dialog).toHaveCount(0)
    await tokens.openForm()
    await page.mouse.click(5, 5)

    await expect(tokens.at.dialog).toHaveCount(0)
    expect(api.all('post', personalAccessTokensPaths.mine)).toHaveLength(0)
  })

  test('a double click on Create sends one create', { tag: '@PAT-1' }, async ({ page, api }) => {
    let answer!: () => void
    const answered = new Promise<void>((resolve) => (answer = resolve))
    api.mock(personalAccessTokensApi.mineAre([]), { ...personalAccessTokensApi.creates(aToken().named('Laptop').created(value)), heldUntil: answered })
    const tokens = personalAccessTokensPage(page)
    await tokens.open()
    await tokens.openForm()
    await tokens.at.nameField.fill('Laptop')

    await tokens.at.create.dblclick()
    answer()

    await expect(tokens.at.value(value)).toBeVisible()
    expect(api.all('post', personalAccessTokensPaths.mine)).toHaveLength(1)
  })

  test('Back while the value shows asks first, and Stay keeps the value', { tag: '@PAT-1' }, async ({ page, api }) => {
    api.mock(personalAccessTokensApi.mineAre([]), personalAccessTokensApi.creates(aToken().named('Laptop').created(value)))
    const tokens = personalAccessTokensPage(page)
    await page.goto('/settings')
    await tokens.at.settingsLink.click()
    await expect(tokens.at.heading).toBeVisible()
    await tokens.createNamed('Laptop')
    const tokenDialogId = await tokens.at.dialog.getAttribute('id')

    await page.goBack()

    await expect(tokens.at.leaveQuestion).toBeVisible()
    expect(await tokens.at.leaveQuestion.getAttribute('id')).not.toBe(tokenDialogId)
    await tokens.at.stay.click()
    await expect(tokens.at.leaveQuestion).toHaveCount(0)
    await expect(tokens.at.value(value)).toBeVisible()
    await expect(page).toHaveURL(/\/settings\/personal-access-tokens$/)
    await page.goBack()
    await tokens.at.leave.click()
    await expect(page).toHaveURL(/\/settings$/)
    await expect(tokens.at.anyTokenValue).toHaveCount(0)
  })

  test('a second Back while the question shows leaves, and the address names the page on the screen', { tag: '@PAT-1' }, async ({ page, api }) => {
    api.mock(personalAccessTokensApi.mineAre([]), personalAccessTokensApi.creates(aToken().named('Laptop').created(value)))
    const tokens = personalAccessTokensPage(page)
    await tokens.openAfterAboutAndSettings()
    await tokens.createNamed('Laptop')
    await page.goBack()
    await expect(tokens.at.leaveQuestion).toBeVisible()

    await page.goBack()

    await expect(tokens.at.aboutHeading).toBeVisible()
    await expect(page).toHaveURL(/\/about$/)
    await expect(tokens.at.leaveQuestion).toHaveCount(0)
    await expect(tokens.at.anyTokenValue).toHaveCount(0)
  })

  test('Back while a create runs asks first, also a Back at the click on Create', { tag: '@PAT-1' }, async ({ page, api }) => {
    let answer!: () => void
    const answered = new Promise<void>((resolve) => (answer = resolve))
    api.mock(personalAccessTokensApi.mineAre([]), { ...personalAccessTokensApi.creates(aToken().named('Laptop').created(value)), heldUntil: answered })
    const tokens = personalAccessTokensPage(page)
    await tokens.openAfterAboutAndSettings()
    await tokens.openForm()
    await tokens.at.nameField.fill('Laptop')

    await tokens.createAndGoBackAtOnce()

    await expect(tokens.at.leaveQuestion).toBeVisible()
    await tokens.at.stay.click()
    await expect(tokens.at.cancel).toBeDisabled()
    await page.goBack()
    await expect(tokens.at.leaveQuestion).toBeVisible()
    await tokens.at.stay.click()
    answer()
    await expect(tokens.at.value(value)).toBeVisible()
  })

  test('Forward while the question shows closes it on the page, and the next Back asks again', { tag: '@PAT-1' }, async ({ page, api }) => {
    api.mock(personalAccessTokensApi.mineAre([]), personalAccessTokensApi.creates(aToken().named('Laptop').created(value)))
    const tokens = personalAccessTokensPage(page)
    await tokens.openAfterAboutAndSettings()
    await tokens.createNamed('Laptop')
    await page.goBack()
    await expect(tokens.at.leaveQuestion).toBeVisible()

    await page.goForward()

    await expect(tokens.at.leaveQuestion).toHaveCount(0)
    await expect(tokens.at.value(value)).toBeVisible()
    await expect(page).toHaveURL(/\/settings\/personal-access-tokens$/)
    await page.goBack()
    await expect(tokens.at.leaveQuestion).toBeVisible()
    await tokens.at.leave.click()
    await expect(page).toHaveURL(/\/settings$/)
    await expect(tokens.at.anyTokenValue).toHaveCount(0)
  })

  for (const exit of ['Cancel', 'Escape', 'a click outside'] as const) {
    test(`${exit} at the click on Create keeps the dialog open, and then shows the value`, { tag: '@PAT-1' }, async ({ page, api }) => {
      let answer!: () => void
      const answered = new Promise<void>((resolve) => (answer = resolve))
      api.mock(personalAccessTokensApi.mineAre([]), { ...personalAccessTokensApi.creates(aToken().named('Laptop').created(value)), heldUntil: answered })
      const tokens = personalAccessTokensPage(page)
      await tokens.open()
      await tokens.openForm()
      await tokens.at.nameField.fill('Laptop')

      await tokens.createAndCloseAtOnce(exit)

      await expect(tokens.at.cancel).toBeDisabled()
      answer()
      await expect(tokens.at.value(value)).toBeVisible()
      expect(api.all('post', personalAccessTokensPaths.mine)).toHaveLength(1)
    })
  }

  test('Back from an empty form, and Back after Done, leave with no question', { tag: '@PAT-1' }, async ({ page, api }) => {
    api.mock(personalAccessTokensApi.mineAre([]), personalAccessTokensApi.creates(aToken().named('Laptop').created(value)))
    const tokens = personalAccessTokensPage(page)
    await tokens.openAfterAboutAndSettings()
    await tokens.openForm()

    await page.goBack()

    await expect(page).toHaveURL(/\/settings$/)
    await expect(tokens.at.leaveQuestion).toHaveCount(0)
    await tokens.at.settingsLink.click()
    await tokens.createNamed('Laptop')
    await tokens.at.done.click()
    await expect(tokens.at.dialog).toHaveCount(0)

    await page.goBack()

    await expect(page).toHaveURL(/\/settings$/)
    await expect(tokens.at.leaveQuestion).toHaveCount(0)
  })

  test('a close of the tab asks while the value shows, and not after Done', { tag: '@PAT-1' }, async ({ page, api }) => {
    api.mock(personalAccessTokensApi.mineAre([]), personalAccessTokensApi.creates(aToken().named('Laptop').created(value)))
    const tokens = personalAccessTokensPage(page)
    await tokens.open()
    await tokens.createNamed('Laptop')

    expect(await tokens.warnsAtAClose()).toBe(true)
    await tokens.at.done.click()
    await expect(tokens.at.dialog).toHaveCount(0)
    expect(await tokens.warnsAtAClose()).toBe(false)
  })

  test('a create that fails can be sent again, and then shows the value', { tag: '@PAT-1' }, async ({ page, api }) => {
    api.mock(personalAccessTokensApi.mineAre([]), personalAccessTokensApi.creates(aToken().named('Laptop').created(value)))
    api.mock({ method: 'post', path: personalAccessTokensPaths.mine, status: 500, body: aProblem().build(), once: true })
    const tokens = personalAccessTokensPage(page)
    await tokens.open()
    await tokens.createNamed('Laptop')
    await expect(tokens.at.nameField).toBeVisible()

    await tokens.create()

    await expect(tokens.at.value(value)).toBeVisible()
    expect(api.all('post', personalAccessTokensPaths.mine)).toHaveLength(2)
  })

  test('a sign-out in another tab while the value shows leaves with no prompt of the browser', { tag: '@PAT-1' }, async ({ page, api }) => {
    api.mock(personalAccessTokensApi.mineAre([]), personalAccessTokensApi.creates(aToken().named('Laptop').created(value)))
    const tokens = personalAccessTokensPage(page)
    await tokens.open()
    await tokens.createNamed('Laptop')
    await expect(tokens.at.value(value)).toBeVisible()
    api.mock(authApi.loginOpens())
    const prompts = tokens.countThePrompts()

    await tokens.signOutInAnotherTab()

    await expect.poll(() => page.url()).toContain('/auth/login?signedOut=1')
    expect(prompts()).toBe(0)
  })

  test('Never sends neverExpires, and a past date shows the message of the Server and sends nothing', { tag: '@PAT-8' }, async ({ page, api }) => {
    const forever = aToken().named('Forever').neverExpiring()
    api.mock(personalAccessTokensApi.mineAre([]), personalAccessTokensApi.creates(forever.created(value)))
    const tokens = personalAccessTokensPage(page)
    await tokens.openAt(instantIn('2026-11-16T10:00:00', browserZone()))
    await tokens.openForm()
    await tokens.at.nameField.fill('Forever')

    await tokens.pickExpiry('On a date that I pick')
    await tokens.typeLastDay('11152026')
    await tokens.at.create.click()

    await expect(tokens.at.fieldError('Pick an expiry date in the future, or pick Never.')).toBeVisible()
    expect(api.all('post', personalAccessTokensPaths.mine)).toHaveLength(0)

    await tokens.pickExpiry('Never')
    await tokens.create()

    expect(api.last('post', personalAccessTokensPaths.mine)?.body).toEqual({
      name: 'Forever',
      permissions: [],
      readJournal: false,
      writeJournal: false,
      neverExpires: true,
    })
    await expect(tokens.at.dialog).toContainText('It works until you revoke it.')
  })

  test('a picked date sends the end of that day in the zone of the browser', { tag: '@PAT-8' }, async ({ page, api }) => {
    api.mock(personalAccessTokensApi.mineAre([]), personalAccessTokensApi.creates(aToken().named('Trip').created(value)))
    const tokens = personalAccessTokensPage(page)
    await tokens.openAt(instantIn('2026-11-16T10:00:00', browserZone()))
    await tokens.openForm()
    await tokens.at.nameField.fill('Trip')

    await tokens.pickExpiry('On a date that I pick')
    await tokens.typeLastDay('01312027')
    await tokens.create()

    expect(api.last('post', personalAccessTokensPaths.mine)?.body).toMatchObject({ expiresAt: endOfDayIn('2027-02-01') })
  })

  test('offers only the permissions of the person, in words, and Change my journal ticks Read my journal', { tag: ['@PAT-3', '@PAT-5'] }, async ({ page, api }) => {
    api.mock(
      membershipApi.userHolds('manage_model_providers', 'read_users'),
      personalAccessTokensApi.mineAre([]),
      personalAccessTokensApi.creates(aToken().named('Script').created(value)),
    )
    const tokens = personalAccessTokensPage(page)
    await tokens.open()
    await tokens.openForm()

    await expect(tokens.at.permission('See the users')).toBeVisible()
    await expect(tokens.at.permission('Manage the model providers')).toBeVisible()
    expect(await tokens.at.permissionChoices.count()).toBe(2)
    await expect(tokens.at.dialog).not.toContainText('read_users')
    await expect(tokens.at.dialog).not.toContainText('manage_')
    await tokens.at.nameField.fill('Script')
    await tokens.at.permission('See the users').click({ force: true })
    await tokens.at.changeJournal.click({ force: true })
    await expect(tokens.at.readJournal).toBeChecked()
    await tokens.create()

    expect(api.last('post', personalAccessTokensPaths.mine)?.body).toMatchObject({
      name: 'Script',
      permissions: ['read_users'],
      readJournal: true,
      writeJournal: true,
    })
  })

  test('the Owner can give a token the job tools, and the list says so', { tag: '@PAT-21' }, async ({ page, api }) => {
    const claude = aToken().named('Claude Code').withTheJobTools().neverExpiring()
    api.mock(
      usersApi.currentIs({ identifier: actorSubject, name: 'Ana Admin', isOwner: true }),
      personalAccessTokensApi.mineAre([]),
      personalAccessTokensApi.creates(claude.created(value)),
    )
    const tokens = personalAccessTokensPage(page)
    await tokens.open()
    await tokens.openForm()
    await tokens.at.nameField.fill('Claude Code')
    await tokens.pickExpiry('Never')

    await tokens.at.jobTools.click({ force: true })
    api.mock(personalAccessTokensApi.mineAre([claude]))
    await tokens.create()

    expect(api.last('post', personalAccessTokensPaths.mine)?.body).toEqual({
      name: 'Claude Code',
      permissions: [],
      readJournal: false,
      writeJournal: false,
      jobTools: true,
      neverExpires: true,
    })
    await tokens.at.done.click()
    await expect(tokens.at.row('Claude Code')).toContainText('Can use the job tools of the Owner')
  })

  test('a person who is not the Owner gets no job tools choice', { tag: '@PAT-21' }, async ({ page, api }) => {
    api.mock(personalAccessTokensApi.mineAre([aToken().named('Laptop')]))
    const tokens = personalAccessTokensPage(page)
    await tokens.open()

    await tokens.openForm()

    await expect(tokens.at.readJournal).toBeVisible()
    await expect(tokens.at.jobTools).toHaveCount(0)
    await expect(tokens.at.row('Laptop')).not.toContainText('job tools')
  })

  test('a person with no permission gets no permission choice', async ({ page, api }) => {
    api.mock(personalAccessTokensApi.mineAre([]))
    const tokens = personalAccessTokensPage(page)
    await tokens.open()

    await tokens.openForm()

    await expect(tokens.at.readJournal).toBeVisible()
    expect(await tokens.at.permissionChoices.count()).toBe(0)
  })

  test('a refused create shows the reason in the form, with no toast, and no value', async ({ page, api }) => {
    const detail = 'This personal access token has no grant to see the users. Make a token that has it.'
    api.mock(personalAccessTokensApi.mineAre([]), personalAccessTokensApi.createRefuses(detail))
    const tokens = personalAccessTokensPage(page)
    await tokens.open()
    await tokens.openForm()
    await tokens.at.nameField.fill('Laptop')

    await tokens.create()

    await expect(tokens.at.refusal).toContainText(detail)
    await expect(tokens.at.nameField).toBeVisible()
    expect(await tokens.at.toasts.count()).toBe(0)
  })

  test('lists each token with its access in words, its dates, and its state', async ({ page, api }) => {
    api.mock(
      personalAccessTokensApi.mineAre([
        aToken().named('Laptop').changingTheJournal().granting('read_users').usedAt('2026-11-10T15:30:00Z'),
        aToken().named('Old script').readingTheJournal().revokedAt('2026-11-05T12:00:00Z'),
        aToken().named('Forever').neverExpiring(),
      ]),
    )
    const tokens = personalAccessTokensPage(page)

    await tokens.open()

    await expect(tokens.at.row('Laptop')).toContainText('Active')
    await expect(tokens.at.row('Laptop')).toContainText('Reads and changes the journal')
    await expect(tokens.at.row('Laptop')).toContainText('Can also see the users')
    await expect(tokens.at.row('Laptop')).toContainText('Last used on Nov')
    await expect(tokens.at.revoke('Laptop')).toBeVisible()
    await expect(tokens.at.row('Old script')).toContainText('Revoked')
    await expect(tokens.at.row('Old script')).toContainText('Reads the journal')
    await expect(tokens.at.revoke('Old script')).toHaveCount(0)
    await expect(tokens.at.row('Forever')).toContainText('No access to the journal')
    await expect(tokens.at.row('Forever')).toContainText('Never expires')
    await expect(tokens.at.row('Forever')).toContainText('Not used yet')
    await expect(tokens.at.list).not.toContainText('read_users')
  })

  test('revokes a token after a confirm, and moves the focus to the heading', async ({ page, api }) => {
    const laptop = aToken().named('Laptop')
    api.mock(personalAccessTokensApi.mineAre([laptop]), personalAccessTokensApi.revokes(laptop.build().id))
    const tokens = personalAccessTokensPage(page)
    await tokens.open()

    api.mock(personalAccessTokensApi.mineAre([laptop.revokedAt('2026-11-16T10:00:00Z')]))
    await tokens.revoke(laptop.build().id, 'Laptop')

    await expect(tokens.at.row('Laptop')).toContainText('Revoked')
    await expect(tokens.at.revoke('Laptop')).toHaveCount(0)
    await expect(tokens.at.toast('Token revoked')).toBeVisible()
    expect(await tokens.at.toasts.count()).toBe(1)
    await expect(tokens.at.heading).toBeFocused()
  })

  test('says that the list is empty', async ({ page, api }) => {
    api.mock(personalAccessTokensApi.mineAre([]))
    const tokens = personalAccessTokensPage(page)

    await tokens.open()

    await expect(tokens.at.empty).toBeVisible()
  })

  test('the sidebar and the Settings page link to the page', async ({ page, api }) => {
    api.mock(personalAccessTokensApi.mineAre([]))
    const tokens = personalAccessTokensPage(page)
    await page.goto('/settings')

    await tokens.at.settingsLink.click()

    await expect(tokens.at.heading).toBeVisible()
    await expect(tokens.at.navLink).toHaveAttribute('aria-current', 'page')
  })
})

test.describe('the tokens of another user, for an Admin', () => {
  test.use({ actor: as.user('Ana Admin') })

  test('lists the tokens of the user with Revoke and no value, and revokes one', { tag: '@PAT-13' }, async ({ page, api }) => {
    const grace = aUser().named('Grace Hopper').seenByAdmin()
    const { id } = grace.build()
    const laptop = aToken().named('Grace laptop').of(id).readingTheJournal()
    api.mock(
      membershipApi.userMayReadUsers(),
      usersApi.oneIs(grace),
      personalAccessTokensApi.ofUserAre(id, [laptop, aToken().named('Grace phone').of(id).notRevocable()]),
      personalAccessTokensApi.revokes(laptop.build().id),
    )
    const tokens = personalAccessTokensPage(page)
    await tokens.openUser(id, 'Grace Hopper')

    await expect(tokens.at.userRow('Grace Hopper', 'Grace laptop')).toContainText('Reads the journal')
    await expect(tokens.at.revoke('Grace phone')).toHaveCount(0)
    await expect(tokens.at.anyTokenValue).toHaveCount(0)
    api.mock(personalAccessTokensApi.ofUserAre(id, [laptop.revokedAt('2026-11-16T10:00:00Z')]))
    await tokens.revoke(laptop.build().id, 'Grace laptop')

    await expect(tokens.at.userRow('Grace Hopper', 'Grace laptop')).toContainText('Revoked')
    await expect(tokens.at.userHeading('Grace Hopper')).toBeFocused()
  })

  test('says that the user has no token', async ({ page, api }) => {
    const grace = aUser().named('Grace Hopper')
    api.mock(usersApi.oneIs(grace), personalAccessTokensApi.ofUserAre(grace.build().id, []))
    const tokens = personalAccessTokensPage(page)

    await tokens.openUser(grace.build().id, 'Grace Hopper')

    await expect(tokens.at.userEmpty('Grace Hopper')).toBeVisible()
  })
})

test.describe('the personal access tokens on a 390 px phone', () => {
  test.use({ actor: as.user('Ana Admin'), viewport: { width: 390, height: 844 } })

  test('the created value and the list fit the window with no sideways scroll', async ({ page, api }) => {
    const laptop = aToken().named('L'.repeat(100)).changingTheJournal().granting('read_users')
    api.mock(personalAccessTokensApi.mineAre([laptop]), personalAccessTokensApi.creates(laptop.created(value)))
    const tokens = personalAccessTokensPage(page)
    await tokens.open()
    expect(await tokens.pageOverflow()).toBe(0)
    await tokens.openForm()
    await tokens.at.nameField.fill('Phone')

    await tokens.create()

    await expect(tokens.at.value(value)).toBeVisible()
    const box = (await tokens.at.value(value).boundingBox())!
    expect(box.x).toBeGreaterThanOrEqual(0)
    expect(box.x + box.width).toBeLessThanOrEqual(390)
    for (const button of [tokens.at.copy, tokens.at.done]) {
      expect((await button.boundingBox())!.height).toBeGreaterThanOrEqual(44)
    }
    expect(await tokens.pageOverflow()).toBe(0)
  })
})
