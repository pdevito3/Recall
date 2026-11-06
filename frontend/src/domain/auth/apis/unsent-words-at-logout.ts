import { useSyncExternalStore } from 'react'

import { writePointer } from '@/domain/sessions/autosave/composer-pointer'
import { discardUnsentDrafts, settleDraftSavers, stopDraftSavers } from '@/domain/sessions/autosave/draft-saver'
import { settleSettingSaver, stopSettingSaver } from '@/domain/settings/apis/user-setting'

type Choice = 'keep' | 'discard'

const settleLimitMs = 5_000

let answer: ((choice: Choice) => void) | null = null
const listeners = new Set<() => void>()

const subscribe = (listener: () => void) => {
  listeners.add(listener)
  return () => {
    listeners.delete(listener)
  }
}

const setAnswer = (next: typeof answer) => {
  answer = next
  for (const listener of listeners) listener()
}

export const useIsAskingAboutUnsentWords = () => useSyncExternalStore(subscribe, () => answer !== null)

export function answerAboutUnsentWords(choice: Choice) {
  answer?.(choice)
  setAnswer(null)
}

const withinLimit = (settled: Promise<boolean>) =>
  Promise.race([settled, new Promise<boolean>((resolve) => setTimeout(() => resolve(false), settleLimitMs))])

/**
 * Sends the unsent words before a logout. When a word stays unsent, the person chooses to keep or discard the words.
 * Kept words stay in the outbox under the key of this person, and they go out at the next sign-in of this person.
 * The answer runs after the Server ends the session. A logout that fails keeps each saver running.
 */
export async function settleUnsentWords(): Promise<() => void> {
  const [areWordsSent] = await Promise.all([withinLimit(settleDraftSavers()), withinLimit(settleSettingSaver())])
  const choice = areWordsSent ? 'keep' : await new Promise<Choice>((resolve) => setAnswer(resolve))
  return () => {
    stopDraftSavers()
    stopSettingSaver()
    if (choice === 'discard') discardUnsentDrafts()
    writePointer(null)
  }
}
