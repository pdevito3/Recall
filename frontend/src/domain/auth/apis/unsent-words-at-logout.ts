import { useSyncExternalStore } from 'react'

import { capturesOf, subscribeToAudioStore, type StoredCapture } from '@/domain/capture/audio-store'
import { progressOf, unsentAudioOf } from '@/domain/capture/upload-plan'
import { endOnServer, forgetCaptures, sendStoredAudioBeforeSignOut } from '@/domain/capture/upload-queue'
import { stopRecordingOfThisTab } from '@/domain/capture/use-capture'
import { writePointer } from '@/domain/sessions/autosave/composer-pointer'
import { removeNoticesOfWordsInHistory, settleDraftSavers, stopDraftSavers, unsentWordsOnThisDevice, type UnsentWords } from '@/domain/sessions/autosave/draft-saver'
import { askOtherTabsToSettle } from '@/domain/sessions/autosave/tab-keys'
import { isAnyTagChangeUnsaved, settleMetadataSavers, stopMetadataSavers } from '@/domain/sessions/autosave/tag-savers'
import { settleSettingSaver, stopSettingSaver } from '@/domain/settings/apis/user-setting'
import { pageUserOf } from '@/lib/page-user'

type Choice = 'keep' | 'discard' | 'stay'

interface Question {
  unsent: UnsentWords[]
  /** The recordings whose audio the Server does not have all of. */
  audio: StoredCapture[]
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

/**
 * Each tab stops its recording and tries to send its words, then the audio goes, and then the person sees all the words
 * and the audio that did not go, also those of another tab. What the person discards leaves this device only after the
 * sign-out works, so a sign-out that fails, as with no network, loses nothing.
 */
export async function settleUnsentWords(beforeAQuestion: () => void): Promise<() => Promise<void>> {
  const [otherTabs] = await Promise.all([
    askOtherTabsToSettle(settleLimitMs),
    withinLimit(settleDraftSavers()),
    withinLimit(settleSettingSaver()),
    withinLimit(settleMetadataSavers()),
    stopRecordingOfThisTab(),
  ])
  const owner = pageUserOf()
  const unsentAudio = owner ? await sendStoredAudioBeforeSignOut(owner).catch((): StoredCapture[] => []) : []
  const unsent = unsentWordsOnThisDevice(pageUserOf(), otherTabs)
  // Tags have no outbox, so a sign-out loses a change of a tag that the Server did not take.
  const isATagUnsaved = isAnyTagChangeUnsaved() || otherTabs.some(({ hasUnsavedTags }) => hasUnsavedTags)
  const hasNoQuestion = unsent.length === 0 && unsentAudio.length === 0 && !isATagUnsaved
  if (!hasNoQuestion) beforeAQuestion()
  const { choice, audio } = hasNoQuestion ? { choice: 'keep' as const, audio: [] } : await ask({ unsent, audio: unsentAudio, isATagUnsaved }, owner)
  if (choice === 'stay') throw new StayedSignedIn()
  // The Server part of a discard needs the session, so it goes before the sign-out ends it.
  if (choice === 'discard') await endOnServer(audio)
  return async () => {
    stopSaversBeforeLeaving()
    if (choice === 'discard') {
      for (const { discard } of unsent) discard()
      await forgetCaptures(audio)
    }
    removeNoticesOfWordsInHistory(pageUserOf())
    writePointer(null)
  }
}
