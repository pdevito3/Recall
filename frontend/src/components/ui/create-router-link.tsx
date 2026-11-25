import type * as React from "react"
import {
  createLink,
  type LinkComponent,
  type LinkComponentProps,
  type NavigateOptions,
} from "@tanstack/react-router"

import type { RouterLinkHostProps } from "@/components/ui/link"

/**
 * Makes a host that renders `RouterLinkPrimitive` a TanStack Router link with
 * typed `to`, `params`, and `search`. The link sets `aria-current="page"` only
 * when it goes to the current page, not to a parent of the current page.
 */
export function createRouterLink<
  THost extends React.ComponentType<RouterLinkHostProps>,
>(Host: THost): LinkComponent<THost> {
  const RouterLink: React.ComponentType<
    LinkComponentProps<THost> & { routerOptions: NavigateOptions }
  > = createLink(Host)
  return (props) => {
    // The returned type checks the props of each caller against its own route.
    const options = props as LinkComponentProps<THost>
    return <RouterLink activeOptions={{ exact: true }} {...options} routerOptions={options} />
  }
}
