/**
 * Only one tab can join a hold, because two joins put the same words in the entry twice. The browser gives the lock to
 * one tab, and gives it to the next tab only when the first tab releases it or closes. Plain HTTP has no Web Locks, so
 * there each tab may join: the words can show twice, and they are never lost.
 */
export function claimHold(name: string, onGranted: () => void): () => void {
  if (!('locks' in navigator)) {
    queueMicrotask(onGranted)
    return () => undefined
  }
  let release = () => undefined as void
  const held = new Promise<void>((resolve) => {
    release = resolve
  })
  const abort = new AbortController()
  let isGranted = false
  navigator.locks
    .request(`recall.hold.${name}`, { signal: abort.signal }, () => {
      isGranted = true
      onGranted()
      return held
    })
    .catch(() => undefined)
  return () => {
    if (!isGranted) abort.abort()
    release()
  }
}
