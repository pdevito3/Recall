import type { AnyRouter } from '@tanstack/react-router'

let hasNavigated = false

export function watchForNavigation(router: AnyRouter) {
  router.subscribe('onBeforeNavigate', ({ fromLocation }) => {
    if (fromLocation) hasNavigated = true
  })
}

export const mayTakeFocusOnThisPage = () => !hasNavigated
