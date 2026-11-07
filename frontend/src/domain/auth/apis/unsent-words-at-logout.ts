import { useSyncExternalStore } from 'react'

import { writePointer } from '@/domain/sessions/autosave/composer-pointer'
import { settleDraftSavers, stopDraftSavers, unsentWordsOnThisDevice, type UnsentWords } from '@/domain/sessions/autosave/draft-saver'
import { askOtherTabsToSettle } from '@/domain/sessions/autosave/tab-keys'
import { isAnyTagChangeUnsaved, settleMetadataSavers, stopMetadataSavers } from '@/domain/sessions/autosave/tag-savers'
import { settleSettingSaver, stopSettingSaver } from '@/domain/settings/apis/user-setting'
import { pageUserOf } from '@/lib/page-user'

type Choice = 'keep' | 'discard' | 'stay'

interface Question {
  unsent: UnsentWords[]
  isATagUnsaved: boolean
  answer: (choice: Choice) => void
}

const settleLimitMs = 5_000

let question: Question | null = null
const listeners = new Set<() => void>()

const subscribe = (listener: () => void) => {
  listeners.add(listener)
  return () => {
    listeners.delete(listener)
  }
}

const setQuestion = (next: Question | null) => {
  question = next
  for (const listener of listeners) listener()
}

export const useQuestionAboutUnsentWords = () => useSyncExternalStore(subscribe, () => question)

export function answerAboutUnsentWords(choice: Choice) {
  question?.answer(choice)
  setQuestion(null)
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

/** Each tab tries to send its words, and then the person sees all the words that did not go, also those of another tab. */
export async function settleUnsentWords(): Promise<() => void> {
  const [otherTabs] = await Promise.all([askOtherTabsToSettle(settleLimitMs), withinLimit(settleDraftSavers()), withinLimit(settleSettingSaver()), withinLimit(settleMetadataSavers())])
  const unsent = unsentWordsOnThisDevice(pageUserOf(), otherTabs)
  // Tags have no outbox, so a sign-out loses a change of a tag that the Server did not take.
  const isATagUnsaved = isAnyTagChangeUnsaved() || otherTabs.some(({ hasUnsavedTags }) => hasUnsavedTags)
  const choice = unsent.length === 0 && !isATagUnsaved ? 'keep' : await new Promise<Choice>((answer) => setQuestion({ unsent, isATagUnsaved, answer }))
  if (choice === 'stay') throw new StayedSignedIn()
  return () => {
    stopSaversBeforeLeaving()
    if (choice === 'discard') for (const { discard } of unsent) discard()
    writePointer(null)
  }
}
