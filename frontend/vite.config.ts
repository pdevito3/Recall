/// <reference types="vitest/config" />
import path from 'path'
import { defineConfig, type Plugin, type ProxyOptions } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import svgr from 'vite-plugin-svgr'
import { playwright } from '@vitest/browser-playwright'
import { TanStackRouterVite } from '@tanstack/router-plugin/vite'
import {
  answerRequests,
  beginTest,
  countHeldRequests,
  emulateReducedMotion,
  holdRequests,
  movePointerOnto,
  pressPointer,
  releasePointer,
  releaseRequests,
  takeSentRequests,
  takeUnansweredRequests,
} from './vitest.commands'
import { firstPageChunks } from './first-page-chunks'

// Vendor libraries that can load on the first page. Each group gets its own
// chunk, so the entry chunk stays small and the browser can cache the
// libraries apart from the app code. A chunk loads only on a page that uses it.
// Rollup also puts the dependencies of a library in its chunk. If the entry
// imports one of them, the full chunk loads on the first page. After you add a
// group, look at the modulepreload links in dist/index.html.
const vendorChunks: Record<string, string[]> = {
  react: ['react', 'react-dom', 'scheduler'],
  router: ['@tanstack/react-router', '@tanstack/router-core', '@tanstack/history'],
  query: ['@tanstack/react-query', '@tanstack/query-core'],
  motion: ['motion', 'framer-motion', 'motion-dom', 'motion-utils'],
  // The router does not split validateSearch, so a route that checks its search with zod loads zod on the first page
  zod: ['zod'],
}

function vendorChunk(id: string): string | undefined {
  const match = id.match(/.*\/node_modules\/((?:@[^/]+\/)?[^/]+)\//)
  if (!match) return undefined
  return Object.keys(vendorChunks).find((chunk) =>
    vendorChunks[chunk].includes(match[1]),
  )
}

const firstPageBudgetKb = 965

const failOverFirstPageBudget: Plugin = {
  name: 'first-page-budget',
  apply: 'build',
  writeBundle(_, bundle) {
    const loaded = firstPageChunks(bundle)
    const totalKb = loaded.reduce((sum, chunk) => sum + chunk.sizeKb, 0)
    if (totalKb > firstPageBudgetKb) {
      this.error(
        `The first page loads ${totalKb.toFixed(2)} kB, over the budget of ${firstPageBudgetKb} kB: ` +
          loaded.map((chunk) => `${chunk.fileName} ${chunk.sizeKb.toFixed(2)} kB`).join(', ') +
          '. Find the module that grew it, and see docs/frontend.md, section "Bundle size".',
      )
    }
    this.info(`The first page loads ${totalKb.toFixed(2)} kB of the budget of ${firstPageBudgetKb} kB.`)
  },
}

const serverTarget = process.env.SERVER_HTTP
const serverTimeoutMs = 30_000

class ServerTimeoutError extends Error {}

const failStalledServerRequests: ProxyOptions['configure'] = (proxy) => {
  proxy.on('proxyReq', (proxyReq, req) => {
    const timer = setTimeout(() => {
      proxyReq.destroy(
        new ServerTimeoutError(
          `The Server did not answer ${req.method} ${req.url} in ${serverTimeoutMs / 1000} s.`,
        ),
      )
    }, serverTimeoutMs)
    proxyReq.on('response', () => clearTimeout(timer))
    proxyReq.on('close', () => clearTimeout(timer))
  })
  proxy.on('error', (err, _req, res) => {
    if (err instanceof ServerTimeoutError && 'req' in res && !res.headersSent) {
      res.writeHead(504, { 'Content-Type': 'text/plain' }).end(err.message)
    }
  })
}

const serverPaths = ['/auth/', '/api', '/backwave', '/mcp']

// With no Server, Vite answers each path with its index.html, so a request of the app gets HTML and no error.

const failServerRequestsWithNoServer: Plugin = {
  name: 'fail-server-requests-with-no-server',
  apply: (_, { command }) => command === 'serve' && !serverTarget,
  configureServer(server) {
    server.middlewares.use((req, res, next) => {
      if (!serverPaths.some((path) => req.url?.startsWith(path))) return next()
      res.writeHead(502, { 'Content-Type': 'text/plain' }).end(
        `The dev server has no Server, so nothing answers ${req.method} ${req.url}. ` +
          'Start the app with Aspire, or set SERVER_HTTP. In a test, answer the request.',
      )
    })
  },
}

// https://vite.dev/config/
export default defineConfig({
  plugins: [
    TanStackRouterVite({ autoCodeSplitting: true }),
    react(),
    tailwindcss(),
    svgr({
      include: '**/*.svg',
      svgrOptions: {
        exportType: 'default',
      },
    }),
    failOverFirstPageBudget,
    failServerRequestsWithNoServer,
  ],
  build: {
    rollupOptions: {
      output: {
        manualChunks: vendorChunk,
      },
    },
  },
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
    },
  },
  // Unit tests (*.test.ts) run in Node. Component tests (*.test.tsx) run in a real headless Chromium.
  // Playwright runs the functional tests in tests/ (playwright.config.ts) and the stack tests in e2e/ (playwright.stack.config.ts).
  test: {
    projects: [
      {
        extends: true,
        test: {
          name: 'unit',
          include: ['src/**/*.test.ts', 'e2e/**/*.test.ts', 'eslint.config.test.js', 'first-page-chunks.test.ts', 'refuse-prettier-hook.test.ts'],
          // A zone far from UTC, so that a test that takes a date from UTC or from the machine fails on every machine.
          // See docs/frontend/testing.md, section "Time zones".
          env: { TZ: 'Pacific/Pago_Pago' },
          // Type-check the test files too, so that a type test (expectTypeOf, @ts-expect-error) fails pnpm test.
          typecheck: {
            enabled: true,
            include: ['src/**/*.test.ts'],
            tsconfig: './tsconfig.app.json',
          },
        },
      },
      {
        extends: true,
        // Vite pre-bundles the test dependencies only when it has no cache, and the cache stays until
        // the installed packages or the optimizer options change. A dependency that Vite finds during
        // the run reloads the test page and fails the test. `force` makes Vite scan the tests on each run.
        // `react-dom/client` (from vitest-browser-react) is found only during the run. The tests that import
        // the router, which imports `react-dom`, made that late find reload the page, so it is listed here.
        optimizeDeps: {
          entries: ['src/**/*.test.tsx'],
          include: ['react-dom/client'],
          force: true,
        },
        test: {
          name: 'browser',
          include: ['src/**/*.test.tsx'],
          setupFiles: ['./src/test/browser-setup.ts'],
          browser: {
            enabled: true,
            // A zone far from UTC, on the other side of UTC from the unit tests. See docs/frontend/testing.md.
            provider: playwright({ contextOptions: { timezoneId: 'Pacific/Kiritimati' } }),
            headless: true,
            instances: [{ browser: 'chromium' }],
            // CI keeps a Playwright trace of each failed test, so that a test that hangs shows the step it waited on.
            // The trace costs about 50% more run time, so a local run does not record it. See docs/frontend/testing.md.
            trace: process.env.CI ? 'retain-on-failure' : 'off',
            commands: {
              answerRequests,
              beginTest,
              countHeldRequests,
              emulateReducedMotion,
              holdRequests,
              movePointerOnto,
              pressPointer,
              releasePointer,
              releaseRequests,
              takeSentRequests,
              takeUnansweredRequests,
            },
          },
        },
      },
    ],
  },
  server: {
    proxy: serverTarget
      ? Object.fromEntries(
          serverPaths.map((path) => [path, { target: serverTarget, changeOrigin: false, configure: failStalledServerRequests }]),
        )
      : undefined,
  },
})
