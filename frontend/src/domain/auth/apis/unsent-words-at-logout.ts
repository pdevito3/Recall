import { useSyncExternalStore } from 'react'

import { writePointer } from '@/domain/sessions/autosave/composer-pointer'
import { discardUnsentDrafts, settleDraftSavers, stopDraftSavers } from '@/domain/sessions/autosave/draft-saver'
import { settleMetadataSavers, stopMetadataSavers } from '@/domain/sessions/autosave/tag-savers'
import { settleSettingSaver, stopSettingSaver } from '@/domain/settings/apis/user-setting'

type Choice = 'keep' | 'discard' | 'stay'

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

/** The person chose to stay and copy the words, so the logout ends with no error. */
export class StayedSignedIn extends Error {}

/** A tab that leaves its session stops each saver first. A saver that still runs would ask at `beforeunload`, and a stay there leaves a hidden page. */
export function stopSaversBeforeLeaving() {
  stopDraftSavers()
  stopSettingSaver()
  stopMetadataSavers()
}

export async function settleUnsentWords(): Promise<() => void> {
  const [areWordsSent] = await Promise.all([withinLimit(settleDraftSavers()), withinLimit(settleSettingSaver()), withinLimit(settleMetadataSavers())])
  const choice = areWordsSent ? 'keep' : await new Promise<Choice>((resolve) => setAnswer(resolve))
  if (choice === 'stay') throw new StayedSignedIn()
  return () => {
    stopSaversBeforeLeaving()
    if (choice === 'discard') discardUnsentDrafts()
    writePointer(null)
  }
}
