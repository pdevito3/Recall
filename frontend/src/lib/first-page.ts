import type { AnyRouter } from '@tanstack/react-router'

let hasNavigated = false

/** The app moves the focus after a navigation (`routes/__root.tsx`), so a page may take the focus only on the first page of a load. */
export function watchForNavigation(router: AnyRouter) {
  router.subscribe('onBeforeNavigate', ({ fromLocation }) => {
    if (fromLocation) hasNavigated = true
  })
}

export const isFirstPageOfLoad = () => !hasNavigated
