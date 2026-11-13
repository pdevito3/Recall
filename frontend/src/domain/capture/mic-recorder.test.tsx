import { afterEach, beforeEach, expect, test, vi } from 'vitest'
import { page, userEvent } from 'vitest/browser'

import { capturesOf, removeCapture } from './audio-store'
import { sliceMs, startRecording, type ActiveRecording } from './mic-recorder'

const owner = 'bob'

// Speech is a tone here: the test fakes the source of the mic, and the recorder and the store are real.
function micGivesATone() {
  vi.spyOn(navigator.mediaDevices, 'getUserMedia').mockImplementation(async () => {
    const context = new AudioContext()
    const tone = context.createOscillator()
    const out = context.createMediaStreamDestination()
    tone.connect(out)
    tone.start()
    return out.stream
  })
}

const storedSlices = async () => (await capturesOf(owner))[0]?.storedSlices ?? 0

const wait = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms))

let recording: ActiveRecording | null = null

// A tab plays audio only after a gesture of the person, and the tone of the fake mic is audio.
beforeEach(async () => {
  await userEvent.click(page.elementLocator(document.body))
})

afterEach(async () => {
  await recording?.stop()
  recording = null
  vi.restoreAllMocks()
  for (const capture of await capturesOf(owner)) await removeCapture(capture.clientRecordingId)
})

/** The browser hides the tab, as it does before it closes it or puts it in the background. */
const hideTheTab = () => vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('hidden')

test('a tab that goes to the background stores the part of a second that it heard', async () => {
  // Arrange
  micGivesATone()
  recording = await startRecording(owner, undefined, () => undefined)
  await wait(200)

  // Act
  hideTheTab()
  document.dispatchEvent(new Event('visibilitychange'))

  // Assert
  await expect.poll(storedSlices, { timeout: sliceMs / 2, interval: 20 }).toBe(1)
})

test('a tab that closes stores the part of a second that it heard', async () => {
  // Arrange
  micGivesATone()
  recording = await startRecording(owner, undefined, () => undefined)
  await wait(200)

  // Act
  hideTheTab()
  window.dispatchEvent(new PageTransitionEvent('pagehide'))

  // Assert
  await expect.poll(storedSlices, { timeout: sliceMs / 2, interval: 20 }).toBe(1)
})

test('a slice that this browser refuses to store stops the recording', async () => {
  // Arrange
  micGivesATone()
  const onSliceFailed = vi.fn()
  const put = IDBObjectStore.prototype.put
  vi.spyOn(IDBObjectStore.prototype, 'put').mockImplementation(function (this: IDBObjectStore, ...args: Parameters<IDBObjectStore['put']>) {
    if (this.name === 'slices') throw new DOMException('The disk is full.', 'QuotaExceededError')
    return put.apply(this, args)
  })

  // Act
  recording = await startRecording(owner, undefined, onSliceFailed)

  // Assert
  const stopped = await Promise.race([recording.ended.then(() => true), wait(3 * sliceMs).then(() => false)])
  expect(stopped).toBe(true)
  expect(onSliceFailed).toHaveBeenCalledOnce()
  expect(await storedSlices()).toBe(0)
})
