import type { StoredCapture } from '@/domain/capture/audio-store'
import { clock } from '@/domain/capture/audio-time'
import { waitsOnlyForItsEnd } from '@/domain/capture/capture-view'
import { unsentMsOf } from '@/domain/capture/upload-plan'

/** The server removed the audio that this part would join, so the part can never upload. */
export const isGone = ({ problem }: StoredCapture) => problem?.kind === 'refused' && problem.canSendAgain === false

export const wordsStartText = 'The words of audio that the server has start when you sign in here again, or on their own about a day later.'

/** What did not go of the audio, and what this browser does with it. `stays` starts the sentence about that: a choice of the person, or a fact. */
export function audioText(audio: StoredCapture[], stays: 'If you keep it, it' | 'It') {
  const missing = audio.filter((capture) => !waitsOnlyForItsEnd(capture))
  const notStarted = missing.length < audio.length ? ` ${wordsStartText}` : ''
  if (missing.length === 0) return notStarted.trim()
  const total = clock(missing.reduce((sum, capture) => sum + unsentMsOf(capture), 0))
  const goneCount = missing.filter(isGone).length
  const keep =
    goneCount === 0
      ? `${stays} stays in this browser and uploads when you sign in here again.`
      : goneCount === missing.length
        ? `${stays} stays in this browser, but it cannot upload, because the server removed the audio that it would join.`
        : `${stays} stays in this browser. The audio that the server can take uploads when you sign in here again, and the rest cannot, because the server removed the audio that it would join.`
  const refused = missing.some(({ problem }) => problem?.kind === 'refused' && problem.canSendAgain !== false) ? ' A recording that the server refused waits on the journal page for you to send it again.' : ''
  return `${total} of audio did not reach the server. ${keep}${refused}${notStarted}`
}
