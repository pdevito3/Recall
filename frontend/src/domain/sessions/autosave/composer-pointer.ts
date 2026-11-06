const resumeWithinMs = 60 * 60 * 1000
const pointerKey = 'recall.composer'

export interface ComposerPointer {
  owner: string
  clientSessionId: string
  startedAt: string
  sessionId: string | null
  touchedAt: number
}

function readStored(): ComposerPointer | null {
  try {
    return JSON.parse(sessionStorage.getItem(pointerKey) ?? 'null') as ComposerPointer | null
  } catch {
    return null
  }
}

export function readPointer(owner: string): ComposerPointer | null {
  const pointer = readStored()
  return pointer?.owner === owner && Date.now() - pointer.touchedAt < resumeWithinMs ? pointer : null
}

export function writePointer(pointer: ComposerPointer | null) {
  try {
    if (pointer) sessionStorage.setItem(pointerKey, JSON.stringify(pointer))
    else sessionStorage.removeItem(pointerKey)
  } catch {
    return
  }
}

/** A create can answer after the composer has left the page, and a reload then needs this ID to open the words. */
export function pointAtCreatedSession(clientSessionId: string, sessionId: string) {
  const pointer = readStored()
  if (pointer?.clientSessionId === clientSessionId) writePointer({ ...pointer, sessionId })
}

/** The entry of the pointer was deleted, so the composer now writes a new entry. */
export function pointAtNewEntry(oldClientSessionId: string, clientSessionId: string) {
  const pointer = readStored()
  if (pointer?.clientSessionId === oldClientSessionId) writePointer({ ...pointer, clientSessionId, sessionId: null })
}
