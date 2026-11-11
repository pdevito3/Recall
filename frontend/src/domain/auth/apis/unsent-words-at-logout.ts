import { useSyncExternalStore } from 'react'

import { capturesOf, subscribeToAudioStore, type StoredCapture } from '@/domain/capture/audio-store'
import { progressOf, unsentAudioOf } from '@/domain/capture/upload-plan'
import { endOnServer, forgetCaptures, sendStoredAudioBeforeSignOut } from '@/domain/capture/upload-queue'
import { stopRecordingOfThisTab } from '@/domain/capture/use-capture'
import { writePointer } from '@/domain/sessions/autosave/composer-pointer'
import { losesOnlyNamedWords, removeNoticesOfWordsInHistory, settleDraftSavers, stopDraftSavers, unsentWordsOnThisDevice, type UnsentWords } from '@/domain/sessions/autosave/draft-saver'
import { askOtherTabsToSettle } from '@/domain/sessions/autosave/tab-keys'
import { isAnyTagChangeUnsaved, settleMetadataSavers, stopMetadataSavers } from '@/domain/sessions/autosave/tag-savers'
import { settleSettingSaver, stopSettingSaver } from '@/domain/settings/apis/user-setting'
import { pageUserOf } from '@/lib/page-user'

type Choice = 'keep' | 'discard' | 'stay' | 'left'

interface Question {
  unsent: UnsentWords[]
  /** The recordings whose audio the Server does not have all of. */
  audio: StoredCapture[]
  isATagUnsaved: boolean
  /** The words changed after an answer, so the person answers the new list. */
  isAskedAgain: boolean
  answer: (choice: Choice) => void
}

const settleLimitMs = 5_000

let question: Question | null = null
let hasLeftTheApp = false
/** The recordings that the person answered about in the sign-out that now leaves, so the way out does not name them again. */
let answeredAudio = new Set<string>()
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

export const isAnsweredAtSignOut = ({ clientRecordingId }: StoredCapture) => answeredAudio.has(clientRecordingId)

/**
 * Asks until the person answers, and follows the audio store meanwhile: audio that reaches the Server leaves the question,
 * and when nothing is left to ask about, the sign-out goes on. Answers the choice and the audio that was in the question then.
 */
function ask(first: Omit<Question, 'answer'>, owner: string | null) {
  return new Promise<{ choice: Choice; audio: StoredCapture[] }>((resolve) => {
    let shown: Question
    let stopFollowing = () => {}
    const answer = (choice: Choice) => {
      stopFollowing()
      resolve({ choice, audio: shown.audio })
    }
    const show = (audio: StoredCapture[]) => {
      shown = { ...first, audio, answer }
      setQuestion(shown)
    }
    show(first.audio)
    if (!owner) return
    stopFollowing = subscribeToAudioStore(() => {
      void capturesOf(owner).then((captures) => {
        const audio = unsentAudioOf(captures)
        if (question !== shown || progressOf(audio) === progressOf(shown.audio)) return
        if (audio.length === 0 && first.unsent.length === 0 && !first.isATagUnsaved) answerAboutUnsentWords('keep')
        else show(audio)
      }, () => undefined)
    })
  })
}

/** Each tab stops its recording and tries to send its words, and then the list names all the words that did not go, also those of another tab. */
async function listUnsentWords(): Promise<Pick<Question, 'unsent' | 'isATagUnsaved'>> {
  const [otherTabs] = await Promise.all([
    askOtherTabsToSettle(settleLimitMs),
    withinLimit(settleDraftSavers()),
    withinLimit(settleSettingSaver()),
    withinLimit(settleMetadataSavers()),
    stopRecordingOfThisTab(),
  ])
  // Tags have no outbox, so a sign-out loses a change of a tag that the Server did not take.
  return { unsent: unsentWordsOnThisDevice(pageUserOf(), otherTabs), isATagUnsaved: isAnyTagChangeUnsaved() || otherTabs.some(({ hasUnsavedTags }) => hasUnsavedTags) }
}

/**
 * The words can change while the person reads the list. So each answer lists again, and the person answers the new list
 * when it loses something that the answered list did not name. The audio goes once before the first question, and the
 * question follows the audio store after that. What the person discards leaves this device only after the sign-out works,
 * so a sign-out that fails, as with no network, loses nothing.
 */
export async function settleUnsentWords(beforeAQuestion: () => void, afterAnAnswer: () => void = () => undefined): Promise<() => Promise<void>> {
  const first = await listUnsentWords()
  const owner = pageUserOf()
  let audio = owner ? await sendStoredAudioBeforeSignOut(owner).catch((): StoredCapture[] => []) : []
  let listed = first
  let choice: Choice = 'keep'
  while (listed.unsent.length > 0 || audio.length > 0 || listed.isATagUnsaved) {
    if (hasLeftTheApp) throw new LeftTheAppFirst()
    beforeAQuestion()
    const { unsent, isATagUnsaved } = listed
    ;({ choice, audio } = await ask({ unsent, audio, isATagUnsaved, isAskedAgain: listed !== first }, owner))
    if (choice === 'stay') throw new StayedSignedIn()
    if (choice === 'left') throw new LeftTheAppFirst()
    afterAnAnswer()
    const now = await listUnsentWords()
    if (losesOnlyNamedWords(unsent, now.unsent) && (isATagUnsaved || !now.isATagUnsaved)) break
    listed = now
  }
  const { unsent } = listed
  if (choice === 'discard') {
    // The Server part of a discard needs the session, so it goes before the sign-out ends it.
    await endOnServer(audio)
    // The session ended meanwhile, and the way out that it took shows this audio as kept in this browser.
    if (hasLeftTheApp) throw new LeftTheAppFirst()
  }
  return async () => {
    answeredAudio = new Set(audio.map(({ clientRecordingId }) => clientRecordingId))
    if (choice === 'discard') {
      for (const { discard } of unsent) discard()
      await forgetCaptures(audio)
    }
    removeNoticesOfWordsInHistory(pageUserOf())
    writePointer(null)
  }
}
