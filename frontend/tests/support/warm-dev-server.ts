import { chromium, type FullConfig, type Page } from '@playwright/test'

type RouteNode = { options: Partial<Record<string, { preload?: () => Promise<unknown> }>>; children?: Record<string, RouteNode> | RouteNode[] }

async function openTheHtmlButNotTheApp(page: Page, url: string) {
  await page.route('**/src/main.tsx', (route) => route.fulfill({ contentType: 'text/javascript', body: '' }))
  await page.goto(url)
}

// page.evaluate has no time limit, and the run has no global one. So a module request that stalls would hang the run with no message.
const warmUpMs = 120_000

async function inTheWarmUpTime<T>(work: Promise<T>): Promise<T> {
  let timer: NodeJS.Timeout | undefined
  const late = new Promise<never>((_, reject) => {
    timer = setTimeout(() => reject(new Error(`The dev server did not compile the routes in ${warmUpMs / 1000} s.`)), warmUpMs)
  })
  try {
    return await Promise.race([work, late])
  } finally {
    clearTimeout(timer)
  }
}

export default async function warmDevServer(config: FullConfig) {
  const browser = await chromium.launch()
  try {
    const page = await browser.newPage()
    await openTheHtmlButNotTheApp(page, config.projects[0].use.baseURL!)
    const compiled = page.evaluate(async (routeTreeModule) => {
      const { routeTree } = (await import(routeTreeModule)) as { routeTree: RouteNode }
      const routes: RouteNode[] = []
      const collect = (route: RouteNode) => {
        routes.push(route)
        Object.values(route.children ?? {}).forEach(collect)
      }
      collect(routeTree)
      const lazyParts = ['component', 'errorComponent', 'pendingComponent', 'notFoundComponent']
      await Promise.all(routes.flatMap((route) => lazyParts.map((part) => route.options[part]?.preload?.())))
    }, '/src/routeTree.gen.ts')
    await inTheWarmUpTime(compiled)
  } finally {
    await browser.close()
  }
}
