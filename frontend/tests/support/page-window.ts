/** The part of the Navigation API that a task reads. TypeScript 5.9 has no types for it. */
interface NavigateEvent extends Event {
  readonly destination: { readonly url: string }
}

interface Navigation {
  addEventListener(type: 'navigate', listener: (event: NavigateEvent) => void): void
}

declare global {
  interface Window {
    readonly navigation: Navigation
    zoneOfTheSystem: string | null
    /** `page.exposeFunction` puts each report on the window before the scripts of the page run. */
    reportRender: () => void
    reportLoginNavigation: () => void
    reportToast: () => void
    firstLoadMark?: boolean
    markedPage?: boolean
    markedEditor?: Element
    wordsOfAnotherTabShown?: string[]
  }
}
