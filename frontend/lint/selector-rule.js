// Oxlint does not have the no-restricted-syntax rule of ESLint, so this rule does its work.
export const selectorRule = (entries) => ({
  meta: { type: 'problem', schema: [] },
  create: (context) => Object.fromEntries(entries.map(({ selector, message }) => [selector, (node) => context.report({ node, message })])),
})
