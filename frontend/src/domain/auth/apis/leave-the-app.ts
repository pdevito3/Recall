import { useSyncExternalStore } from 'react'

import { wordsOfThisTabOnlyInMemory } from '@/domain/sessions/autosave/draft-saver'

import { stopSaversBeforeLeaving } from './unsent-words-at-logout'

export const signInForAnotherPersonPage = '/auth/login?anotherPerson=1'

/** Each way out of the signed-in app. A session that ends with no message to this tab (a 401, a revoke, an expiry, a restore of an ended page) is `sessionEnded`. */
export type Departure = { why: 'anotherPerson' } | { why: 'signedOut' | 'signedOutInAnotherTab' | 'sessionEnded'; to: string }

export type LeftTheApp =
  | { why: 'anotherPerson'; hasLostWords: boolean }
  | { why: Exclude<Departure['why'], 'anotherPerson'>; words: string[]; leave: () => void }

let left: LeftTheApp | null = null
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

/**
 * The one way out of the signed-in app. Words that only the memory of this tab holds have no other place, so the tab
 * shows them alone with a Copy before it goes, and goes only when the person says so. Another person must not read
 * them, so for that reason the words go at once, and the next page says that they are gone. A later reason to leave
 * keeps a shown screen of words, except another person.
 */
export function leaveTheApp(departure: Departure) {
  if (left && (departure.why !== 'anotherPerson' || left.why === 'anotherPerson')) return
  // A stopped saver holds no words, so this reads them first. The words of a shown screen are only on that screen.
  const words = [...wordsOfThisTabOnlyInMemory(), ...(left?.words ?? [])]
  stopSaversBeforeLeaving()
  window.removeEventListener('beforeunload', warnOfTheLoss)
  if (departure.why === 'anotherPerson') {
    show({ why: 'anotherPerson', hasLostWords: words.length > 0 })
    window.location.replace(words.length > 0 ? `${signInForAnotherPersonPage}&wordsLost=1` : signInForAnotherPersonPage)
  } else if (words.length > 0) {
    const { why, to } = departure
    window.addEventListener('beforeunload', warnOfTheLoss)
    show({ why, words, leave: () => {
      window.removeEventListener('beforeunload', warnOfTheLoss)
      window.location.replace(to)
    } })
    showPage()
  } else {
    hidePage()
    window.location.replace(departure.to)
  }
}
