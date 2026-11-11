import { signInForAnotherPersonPage } from '../apis/leave-the-app'

export function PageOfAnotherPerson({ hasLostWords }: { hasLostWords: boolean }) {
  return (
    <main className="flex min-h-svh items-center justify-center p-4">
      <div role="alert" className="flex max-w-md flex-col items-start gap-3 rounded-lg border bg-card p-4">
        <p>
          Another person signed in on this browser, so this page closed.
          {hasLostWords ? ' This page held words that this browser had no room to keep, and they are gone.' : ' Any unsent words of this page wait on this device.'}
        </p>
        <a className="font-medium underline underline-offset-4" href={hasLostWords ? `${signInForAnotherPersonPage}&wordsLost=1` : signInForAnotherPersonPage}>Sign in</a>
      </div>
    </main>
  )
}
