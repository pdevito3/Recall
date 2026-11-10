import * as React from "react"

/**
 * An overlay of the layout in a chunk apart from the first page, because its animations bring `motion`. The chunk
 * starts to load at once, so the overlay is ready when a person opens it, also after the network goes. Until the
 * chunk arrives the overlay renders nothing, so use this only for an overlay that is closed on the first paint, and
 * import its trigger from a module with no `motion`.
 */
export function lazyOverlay<Props extends object>(load: () => Promise<React.ComponentType<Props>>) {
  void load().catch(() => undefined)
  const Overlay = React.lazy(async () => ({ default: await load() }))

  return function LazyOverlay(props: Props) {
    return (
      <React.Suspense fallback={null}>
        <Overlay {...props} />
      </React.Suspense>
    )
  }
}
