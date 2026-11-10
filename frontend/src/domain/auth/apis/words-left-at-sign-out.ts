import { useSyncExternalStore } from 'react'

export interface WordsLeftAtSignOut {
  /** Each text that only the memory of this page holds. */
  words: string[]
  leave: () => void
}

let left: WordsLeftAtSignOut | null = null
const listeners = new Set<() => void>()

const subscribe = (listener: () => void) => {
  listeners.add(listener)
  return () => {
    listeners.delete(listener)
  }
}

export const wordsLeftAtSignOut = () => left

export const useWordsLeftAtSignOut = () => useSyncExternalStore(subscribe, wordsLeftAtSignOut)

export function showWordsLeftAtSignOut(words: string[], leave: () => void) {
  left = { words, leave }
  for (const listener of listeners) listener()
}
