// R13 mic card on `/`: one tap records into a new entry, the audio outlives the page, and its words reach the journal.
// The fake mic of browse.mjs beeps, and a beep has no words. To prove the words, give a WAV of speech:
//   node $S/scripts/browse.mjs $S/recipes/mic.mjs --fake-mic <speech.wav>
export const needsMic = true

export default async ({ page, context, expect, step, capture, login, speech }) => {
  const card = page.getByRole('region', { name: 'Record an entry' })
  // While the card records, it has a second status that says each New paragraph tap.
  const status = card.getByTestId('mic-status')
  // The newest capture of this browser. A slice leaves the store when the Server confirms it, so count the capture row, not the slices.
  const newestCapture = () =>
    page.evaluate(
      () =>
        new Promise((resolve, reject) => {
          const open = indexedDB.open('recall.capture')
          open.onerror = () => reject(open.error)
          open.onsuccess = () => {
            const all = open.result.transaction('captures').objectStore('captures').getAll()
            all.onsuccess = () => resolve(all.result.sort((a, b) => a.startedAt.localeCompare(b.startedAt)).at(-1) ?? null)
            all.onerror = () => reject(all.error)
          }
        }),
    )
  const storedSlices = async () => (await newestCapture())?.storedSlices ?? 0

  await step('login', () => login())

  await step('mic first: the card is above the composer and rests at Tap to record', async () => {
    await page.goto('/')
    await expect(status).toContainText('Tap to record', { timeout: 15_000 })
    const cardBox = await card.boundingBox()
    const composerBox = await page.getByRole('region', { name: 'Write a new entry' }).boundingBox()
    expect(cardBox.y + cardBox.height).toBeLessThanOrEqual(composerBox.y)
    await capture('mic-idle')
  })

  await step('record: one tap records, and each second saves in this browser', async () => {
    await card.getByRole('button', { name: 'Record', exact: true }).click()
    await expect(status).toContainText('Recording', { timeout: 15_000 })
    await expect.poll(storedSlices, { timeout: 15_000 }).toBeGreaterThanOrEqual(5)
    await capture('mic-recording')
  })

  await step('paragraph: New paragraph marks the time of the tap, and the card says it', async () => {
    await card.getByRole('button', { name: 'New paragraph' }).click()
    await expect(card.getByRole('status').filter({ hasText: /^New paragraph at / })).toHaveText(/^New paragraph at \d+:\d\d\.$/)
    await expect(card.getByTestId('paragraph-marks')).toHaveText('1')
    await expect.poll(async () => (await newestCapture())?.paragraphTapsMs?.length).toBe(1)
    await capture('mic-paragraph')
  })

  await step('close: a reload in the middle of the recording keeps the audio, and the next page sends it', async () => {
    await page.reload()
    await expect(status).toContainText(/Sending|Writing the words|Your words are in/, { timeout: 60_000 })
    await capture('mic-after-reload')
  })

  // The first transcription on a new stack can wait for the speech model to download, so the wait is long.
  await step(speech ? 'text: the words of the recording come to a new entry' : 'text: the beep has no words, and the card keeps the audio for Try again', async () => {
    await expect(status).toContainText(/Your words are in|No words came from this audio/, { timeout: 900_000 })
    await capture('mic-result')
    if (!speech) {
      await expect(status).toContainText('The audio is stored, but the server heard no words in it')
      await expect(card.getByRole('button', { name: 'Try again' })).toBeEnabled()
      return
    }
    await expect(status).toContainText('Your words are in')
    const entry = card.getByRole('link', { name: 'Open entry' })
    const id = (await entry.getAttribute('href')).split('/').pop()
    const session = await page.evaluate(async (sessionId) => (await fetch(`/api/v1/sessions/${sessionId}`, { headers: { 'X-CSRF': '1' } })).json(), id)
    expect(session.rawText).not.toBe('')
    await entry.click()
    await expect(page.getByRole('textbox', { name: 'Your words' })).not.toBeEmpty()
    await capture('mic-entry')
  })

  await step('offline: with no network the card says that the audio waits in this browser', async () => {
    await page.goto('/')
    await card.getByRole('button', { name: 'Record', exact: true }).click()
    await expect(status).toContainText('Recording', { timeout: 15_000 })
    await context.setOffline(true)
    await expect.poll(storedSlices, { timeout: 15_000 }).toBeGreaterThanOrEqual(3)
    await card.getByRole('button', { name: 'Stop recording' }).click()
    await expect(status).toContainText('No network. The upload waits', { timeout: 15_000 })
    await capture('mic-offline')
    await context.setOffline(false)
    await expect(status).toContainText(/Writing the words|Your words are in|No words came from this audio/, { timeout: 120_000 })
    await capture('mic-back-online')
  })
}
