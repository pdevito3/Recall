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
