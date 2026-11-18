import { useSyncExternalStore } from 'react'

import { capturesOf, type StoredCapture } from '@/domain/capture/audio-store'
import { unsentAudioOf } from '@/domain/capture/upload-plan'
import { stopRecordingOfThisTab } from '@/domain/capture/use-capture'
import { wordsOfThisTabOnlyInMemory } from '@/domain/sessions/autosave/draft-saver'
import { pageUserOf } from '@/lib/page-user'

import { isAnsweredAtSignOut, stopSaversBeforeLeaving } from './unsent-words-at-logout'

export const signInForAnotherPersonPage = '/auth/login?anotherPerson=1'

export const signedOutPage = '/auth/login?signedOut=1'

export const loginPageFor = (returnUrl: string) => `/auth/login?returnUrl=${encodeURIComponent(returnUrl)}`

/** A load of the same page. A replace with the same URL and a hash only moves to the hash, so the hash stays out. */
export const thisPageAgain = () => window.location.pathname + window.location.search

/**
 * Each way out of the signed-in app. A session that ends with no message to this tab (a 401, a revoke, an expiry, a
 * restore of an ended page, a restore into a newer session) is `sessionEnded`. A restored page with no answer about its
 * session is `sessionUnchecked`, and a load again from the error page is `pageDidNotLoad`.
 */
export type Departure = { why: 'anotherPerson' } | { why: 'signedOut' | 'signedOutInAnotherTab' | 'sessionEnded' | 'sessionUnchecked' | 'pageDidNotLoad'; to: string }

export type LeftTheApp =
  | { why: 'anotherPerson'; hasLostWords: boolean }
  | { why: Exclude<Departure['why'], 'anotherPerson'>; words: string[]; audio: StoredCapture[]; leave: () => void }

let left: LeftTheApp | null = null
/** The way out that this tab took and the words that only it holds, also while it reads the audio that waits. */
let leaving: { why: Departure['why']; words: string[] } | null = null
const listeners = new Set<() => void>()

const subscribe = (listener: () => void) => {
  listeners.add(listener)
  return () => {
    listeners.delete(listener)
  }
}

const show = (next: LeftTheApp) => {
  left = next
  for (const listener of listeners) listener()
}

export const leftTheApp = () => left

export const useLeftTheApp = () => useSyncExternalStore(subscribe, leftTheApp)

/**
 * Opacity and not visibility: `visibility` inherits, and an element with `transition-all` would keep showing it for the
 * time of its transition. Opacity leaves the text to a screen reader, to a search in the page, and to the keyboard, so
 * `inert` closes those.
 */
export function hidePage() {
  document.documentElement.style.opacity = '0'
  document.documentElement.inert = true
}

export function showPage() {
  document.documentElement.style.opacity = ''
  document.documentElement.inert = false
}

const warnOfTheLoss = (event: BeforeUnloadEvent) => event.preventDefault()

let isDepartingNow = false

/** True once this tab goes to a page outside the app. A guard of a page asks nothing then, so no prompt stops the way out. */
export const isDeparting = () => isDepartingNow

function depart(to: string) {
  isDepartingNow = true
  window.location.replace(to)
}

/**
 * The audio of the person of this page that waits in this browser for the Server, except what the person answered about
 * in this sign-out. The recording of this tab stops first, so the mic is off and its last part is in the list. A store
 * that does not open names no audio, and the audio that it may hold stays there.
 */
async function audioThatWaits(owner: string): Promise<StoredCapture[]> {
  try {
    await stopRecordingOfThisTab()
    return unsentAudioOf(await capturesOf(owner)).filter((capture) => !isAnsweredAtSignOut(capture))
  } catch {
    return []
  }
}

/**
 * The one way out of the signed-in app, and the lint rule `no-restricted-syntax` in eslint.config.js refuses each other
 * navigation in src. Words that only the memory of this tab holds have no other place, so the tab shows them alone with
 * a Copy before it goes, and goes only when the person says so. Audio that waits stays in this browser, and the same
 * screen names it, so the person knows that it did not reach the server. Another person must not read the words, so for
 * that reason the words go at once, and the next page says that they are gone. A later reason to leave keeps the way out
 * of an earlier one, except another person. An open question of a sign-out closes, because its dialog goes with the app.
 */
export function leaveTheApp(departure: Departure) {
  if (leaving && (departure.why !== 'anotherPerson' || leaving.why === 'anotherPerson')) return
  // A stopped saver holds no words, so this reads them first. The words of an earlier way out are only on its screen.
  const words = [...wordsOfThisTabOnlyInMemory(), ...(leaving?.words ?? [])]
  const thisWayOut = { why: departure.why, words }
  leaving = thisWayOut
  stopSaversBeforeLeaving()
  window.removeEventListener('beforeunload', warnOfTheLoss)
  if (departure.why === 'anotherPerson') {
    show({ why: 'anotherPerson', hasLostWords: words.length > 0 })
    depart(words.length > 0 ? `${signInForAnotherPersonPage}&wordsLost=1` : signInForAnotherPersonPage)
    return
  }
  // The audio store answers later, and the app must take no word and no tap meanwhile.
  hidePage()
  if (words.length > 0) window.addEventListener('beforeunload', warnOfTheLoss)
  const { why, to } = departure
  const goOrShow = (audio: StoredCapture[]) => {
    // Another person signed in meanwhile, and that way out went at once.
    if (leaving !== thisWayOut) return
    if (words.length === 0 && audio.length === 0) {
      // A navigation that the person or the browser stops leaves this page, and a later way out goes again.
      leaving = null
      depart(to)
      return
    }
    show({ why, words, audio, leave: () => {
      window.removeEventListener('beforeunload', warnOfTheLoss)
      depart(to)
    } })
    showPage()
  }
  // A page that never learned its person, as at a first load with an ended session, has no audio to name.
  const owner = pageUserOf()
  if (owner) void audioThatWaits(owner).then(goOrShow)
  else goOrShow([])
}
