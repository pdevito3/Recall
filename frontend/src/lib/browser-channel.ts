/** A browser can have no BroadcastChannel, or refuse to make one. The app then runs with no word between its tabs. */
export function openChannel(name: string): BroadcastChannel | null {
  try {
    return new BroadcastChannel(name)
  } catch {
    return null
  }
}
