import { useSyncExternalStore } from 'react'

import { writePointer } from '@/domain/sessions/autosave/composer-pointer'
import { losesOnlyNamedWords, removeNoticesOfWordsInHistory, settleDraftSavers, stopDraftSavers, unsentWordsOnThisDevice, type UnsentWords } from '@/domain/sessions/autosave/draft-saver'
import { askOtherTabsToSettle } from '@/domain/sessions/autosave/tab-keys'
import { isAnyTagChangeUnsaved, settleMetadataSavers, stopMetadataSavers } from '@/domain/sessions/autosave/tag-savers'
import { settleSettingSaver, stopSettingSaver } from '@/domain/settings/apis/user-setting'
import { pageUserOf } from '@/lib/page-user'

type Choice = 'keep' | 'discard' | 'stay' | 'left'

interface Question {
  unsent: UnsentWords[]
  isATagUnsaved: boolean
  /** The words changed after an answer, so the person answers the new list. */
  isAskedAgain: boolean
  answer: (choice: Choice) => void
}

const settleLimitMs = 5_000

let question: Question | null = null
let hasLeftTheApp = false
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

export function answerAboutUnsentWords(choice: Exclude<Choice, 'left'>) {
  question?.answer(choice)
  setQuestion(null)
}

const withinLimit = (settled: Promise<boolean>) =>
  Promise.race([settled, new Promise<boolean>((resolve) => setTimeout(() => resolve(false), settleLimitMs))])

/** The person chose to stay and copy the words, so the logout ends with no error. */
export class StayedSignedIn extends Error {}

/** The tab left the app another way before the sign-out ended, and that way shows the words, so the logout ends with no error. */
export class LeftTheAppFirst extends Error {}

/**
 * A tab that leaves its session stops each saver first. A saver that still runs would ask at `beforeunload`, and a stay
 * there leaves a hidden page. Its dialog goes with the app, so an open question ends, and no later one opens.
 */
export function stopSaversBeforeLeaving() {
  stopDraftSavers()
  stopSettingSaver()
  stopMetadataSavers()
  hasLeftTheApp = true
  question?.answer('left')
  setQuestion(null)
}

/** Each tab tries to send its words, and then the list names all the words that did not go, also those of another tab. */
async function listUnsentWords(): Promise<Pick<Question, 'unsent' | 'isATagUnsaved'>> {
  const [otherTabs] = await Promise.all([askOtherTabsToSettle(settleLimitMs), withinLimit(settleDraftSavers()), withinLimit(settleSettingSaver()), withinLimit(settleMetadataSavers())])
  // Tags have no outbox, so a sign-out loses a change of a tag that the Server did not take.
  return { unsent: unsentWordsOnThisDevice(pageUserOf(), otherTabs), isATagUnsaved: isAnyTagChangeUnsaved() || otherTabs.some(({ hasUnsavedTags }) => hasUnsavedTags) }
}

/** The words can change while the person reads the list. So each answer lists again, and the person answers the new list when it loses something that the answered list did not name. */
export async function settleUnsentWords(beforeAQuestion: () => void, afterAnAnswer: () => void = () => undefined): Promise<() => void> {
  const first = await listUnsentWords()
  let listed = first
  let choice: Choice = 'keep'
  while (listed.unsent.length > 0 || listed.isATagUnsaved) {
    if (hasLeftTheApp) throw new LeftTheAppFirst()
    beforeAQuestion()
    const { unsent, isATagUnsaved } = listed
    const isAskedAgain = listed !== first
    choice = await new Promise<Choice>((answer) => setQuestion({ unsent, isATagUnsaved, isAskedAgain, answer }))
    if (choice === 'stay') throw new StayedSignedIn()
    if (choice === 'left') throw new LeftTheAppFirst()
    afterAnAnswer()
    const now = await listUnsentWords()
    if (losesOnlyNamedWords(unsent, now.unsent) && (isATagUnsaved || !now.isATagUnsaved)) break
    listed = now
  }
  const { unsent } = listed
  return () => {
    if (choice === 'discard') for (const { discard } of unsent) discard()
    removeNoticesOfWordsInHistory(pageUserOf())
    writePointer(null)
  }
}
