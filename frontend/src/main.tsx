import '@/lib/zod-config'
import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { QueryClientProvider } from '@tanstack/react-query'
import { RouterProvider, createRouter } from '@tanstack/react-router'
import { NotFound } from '@/components/not-found'
import { PageError } from '@/components/page-error'
import { ThemeProvider } from '@/components/theme-provider'
import { Toaster } from '@/components/toaster'
import { guardPageAgainstEndedSession } from '@/domain/auth/apis/auth-actions'
import { AuthKeys } from '@/domain/auth/apis/auth.keys'
import { leaveThePageOfAnotherPerson } from '@/domain/auth/apis/page-of-another-person'
// Connects the generated API clients to apiClient before the first request.
import { setSessionEndedCheck } from '@/lib/api-client'
import { watchForNavigation } from '@/lib/first-page'
import { setPageUserMismatchHandler } from '@/lib/page-user'
import { createQueryClient } from '@/lib/query-client'
import './index.css'

// Import the generated route tree
import { routeTree } from './routeTree.gen'

// Create a new router instance
const router = createRouter({ routeTree, defaultNotFoundComponent: NotFound, defaultErrorComponent: PageError, scrollRestoration: true })
watchForNavigation(router)

// Register the router instance for type safety
declare module '@tanstack/react-router' {
  interface Register {
    router: typeof router
  }
}

const queryClient = createQueryClient()
// With no cancelRefetch, the 401s of calls that run at the same time wait for one check of the session.
setSessionEndedCheck(async () => {
  await queryClient.invalidateQueries({ queryKey: AuthKeys.user() }, { cancelRefetch: false })
  return queryClient.getQueryData(AuthKeys.user()) === null
})
guardPageAgainstEndedSession(queryClient)

setPageUserMismatchHandler(() => leaveThePageOfAnotherPerson(queryClient))

function preloadEachPageForNoNetwork() {
  for (const route of Object.values(router.routesById)) void router.loadRouteChunk(route).catch(() => undefined)
}

// After the first page is on the screen, so that the code of the other pages does not hold up the code of this one.
const stopWaitingForTheFirstPage = router.subscribe('onResolved', () => {
  stopWaitingForTheFirstPage()
  const whenIdle = 'requestIdleCallback' in window ? window.requestIdleCallback : (load: () => void) => setTimeout(load, 1000)
  whenIdle(preloadEachPageForNoNetwork)
})

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <ThemeProvider defaultTheme="system" storageKey="ui-theme">
      <QueryClientProvider client={queryClient}>
        <RouterProvider router={router} />
        <Toaster />
      </QueryClientProvider>
    </ThemeProvider>
  </StrictMode>,
)
