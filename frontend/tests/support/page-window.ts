// The values that the tasks of the functional suite put on the window of the page, and read back in the page.

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
    /** The zone that Intl names for the system. Null is a zone that the tzdata of the browser does not have. */
    zoneOfTheSystem: string | null
    /** `page.exposeFunction` puts each report on the window before the scripts of the page run. */
    reportRender: () => void
    reportLoginNavigation: () => void
    reportToast: () => void
    /** A load of the page clears each mark. */
    firstLoadMark?: boolean
    markedPage?: boolean
    markedEditor?: Element
    /** Each text of the words of another tab that the page showed, in order. */
    shownWords?: string[]
  }
}
