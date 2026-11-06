import { signInForAnotherPersonPage } from '../apis/page-of-another-person'

/** The browser leaves this page at once. A person sees it only when that navigation fails, for example with no connection. */
export function PageOfAnotherPerson() {
  return (
    <main className="flex min-h-svh items-center justify-center p-4">
      <div role="alert" className="flex max-w-md flex-col items-start gap-3 rounded-lg border bg-card p-4">
        <p>Another person signed in on this browser, so this page closed. Your unsent words wait on this device.</p>
        <a className="font-medium underline underline-offset-4" href={signInForAnotherPersonPage}>Sign in</a>
      </div>
    </main>
  )
}
