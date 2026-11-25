const within = (inner, outer) => inner.range[0] >= outer.range[0] && inner.range[1] <= outer.range[1]

const referencesIn = (sourceCode, node) => {
  const found = []
  const visit = (scope) => {
    found.push(...scope.references.filter((ref) => !ref.isTypeReference && within(ref.identifier, node)))
    scope.childScopes.forEach(visit)
  }
  visit(sourceCode.getScope(node))
  return found
}

const resolve = (scope, name) => {
  for (let at = scope; at; at = at.upper) {
    const variable = at.set.get(name)
    if (variable)
      return variable
  }
  return null
}

const isLocal = (variable) => variable && !['module', 'global'].includes(variable.scope.type)

const initOf = (variable) => {
  const def = variable.defs[0]
  return def?.type === 'Variable' ? def.node.init : null
}

const callWrappers = ['MemberExpression', 'ChainExpression', 'TSNonNullExpression', 'AwaitExpression']

const hookCalledBy = (init) => {
  let node = init
  while (node && callWrappers.includes(node.type))
    node = node.object ?? node.expression ?? node.argument
  return node?.type === 'CallExpression' && node.callee.type === 'Identifier' && /^use[A-Z]/.test(node.callee.name)
    ? node.callee.name
    : null
}

const fieldOfCall = (init) => {
  let node = init
  let field = null
  while (node && callWrappers.includes(node.type)) {
    if (node.type === 'MemberExpression')
      field = node.computed ? null : node.property.name
    node = node.object ?? node.expression ?? node.argument
  }
  return field
}

const isFirstValueName = (name) => /^(initial|default)([A-Z]|$)/.test(name)
const ownStateHooks = new Set(['useState', 'useReducer', 'useRef', 'useId'])

const copiedRoots = (node) => {
  switch (node?.type) {
    case 'Identifier':
      return [node]
    case 'MemberExpression':
      return copiedRoots(node.object)
    case 'ChainExpression':
    case 'TSNonNullExpression':
    case 'TSAsExpression':
    case 'TSSatisfiesExpression':
      return copiedRoots(node.expression)
    case 'LogicalExpression':
      return [...copiedRoots(node.left), ...copiedRoots(node.right)]
    case 'ConditionalExpression':
      return [...copiedRoots(node.consequent), ...copiedRoots(node.alternate)]
    case 'ArrowFunctionExpression':
      return node.body.type === 'BlockStatement' ? [] : copiedRoots(node.body)
    default:
      return []
  }
}

const firstValueAdvice = 'If {{source}} is only the first value and the user owns the state from then on, name it initialX or defaultX.'
const timerCalls = new Set(['setTimeout', 'setInterval', 'requestAnimationFrame'])

const functionNameOf = (fn) => fn.id?.name ?? (fn.parent.type === 'VariableDeclarator' ? fn.parent.id.name : null)

const takesQueryData = (variable, property) => {
  const { id } = variable.defs[0].node
  if (id.type === 'Identifier')
    return (fieldOfCall(initOf(variable)) ?? property) === 'data'
  const field = id.type === 'ObjectPattern' && id.properties.find((p) => p.type === 'Property'
    && (p.value.type === 'AssignmentPattern' ? p.value.left : p.value) === variable.identifiers[0])
  return Boolean(field) && !field.computed && field.key.name === 'data'
}

const noCopiedStateRule = {
  meta: {
    type: 'problem',
    schema: [],
    messages: {
      prop: `This state copies {{source}}, so it keeps the old value after {{source}} changes. Read the value where you use it (docs/frontend/server-state.md, rule 2). Give server data to a form in defaultValues. ${firstValueAdvice.replace('name it', 'name the prop')}`,
      argument: `This state copies {{source}}, so it keeps the old value after {{source}} changes. Read the value where you use it (docs/frontend/server-state.md, rule 2). ${firstValueAdvice.replace('name it', 'name the argument')}`,
      query: 'This state copies {{source}}, so it keeps the old value after the query reads the record again. Read the value where you use it (docs/frontend/server-state.md, rule 2). Give server data to a form in defaultValues.',
    },
  },
  create(context) {
    const { sourceCode } = context
    const sourceOf = (identifier, seen) => {
      const variable = resolve(sourceCode.getScope(identifier), identifier.name)
      if (!isLocal(variable) || seen.has(variable))
        return null
      seen.add(variable)
      const { parent } = identifier
      const property = parent.type === 'MemberExpression' && parent.object === identifier && !parent.computed ? parent.property.name : null
      const isParameter = variable.defs[0]?.type === 'Parameter'
      const init = initOf(variable)
      if ((isParameter && isFirstValueName(variable.name)) || (property && isFirstValueName(property)))
        return null
      if (isParameter)
        return /^use[A-Z]/.test(functionNameOf(variable.scope.block) ?? '')
          ? { kind: 'argument', label: `the argument \`${variable.name}\`` }
          : { kind: 'prop', label: `the prop \`${variable.name}\`` }
      const hook = init && hookCalledBy(init)
      if (hook)
        return ownStateHooks.has(hook) || !takesQueryData(variable, property) ? null : { kind: 'query', label: `the result of \`${hook}\`` }
      return copiedRoots(init).map((root) => sourceOf(root, seen)).find(Boolean) ?? null
    }

    const functionOf = (node) => {
      let parent = node.parent
      while (parent && !['FunctionDeclaration', 'FunctionExpression', 'ArrowFunctionExpression'].includes(parent.type))
        parent = parent.parent
      return parent
    }
    const isTimerCallback = (fn) => fn?.parent.type === 'CallExpression' && fn.parent.arguments.includes(fn)
      && timerCalls.has(fn.parent.callee.name ?? fn.parent.callee.property?.name)
    const sourceOfValue = (node) => copiedRoots(node).map((root) => sourceOf(root, new Set())).find(Boolean)
    const readsSource = (node, source) => Boolean(node)
      && referencesIn(sourceCode, node).some((ref) => sourceOf(ref.identifier, new Set())?.label === source.label)
    const isSetAgainInRenderOrTimer = (node, source) => {
      const setter = node.parent.type === 'VariableDeclarator' && node.parent.id.type === 'ArrayPattern' ? node.parent.id.elements[1] : null
      const variable = setter?.type === 'Identifier' && resolve(sourceCode.getScope(setter), setter.name)
      return Boolean(variable) && variable.references.some(({ identifier }) => {
        const call = identifier.parent
        const fn = call.type === 'CallExpression' && call.callee === identifier && functionOf(call)
        return Boolean(fn) && (fn === functionOf(node) || isTimerCallback(fn)) && readsSource(call.arguments[0], source)
      })
    }

    return {
      "CallExpression:matches([callee.name='useState'], [callee.property.name='useState'])"(node) {
        const source = sourceOfValue(node.arguments[0])
        if (source && !isSetAgainInRenderOrTimer(node, source))
          context.report({ node, messageId: source.kind, data: { source: source.label } })
      },
    }
  },
}

const keyedQueryCalls = 'useQuery|useSuspenseQuery|useInfiniteQuery|useSuspenseInfiniteQuery|queryOptions|infiniteQueryOptions|fetchQuery|prefetchQuery|ensureQueryData|fetchInfiniteQuery|prefetchInfiniteQuery'

const isInClientOptions = (identifier, queryFn) => {
  for (let node = identifier.parent; node && node !== queryFn; node = node.parent)
    if (node.type === 'Property' && !node.computed && node.key.name === 'options')
      return true
  return false
}

const isConstant = (node) => node?.type === 'Literal' || (node?.type === 'TemplateLiteral' && node.expressions.length === 0)
  || (node?.type === 'UnaryExpression' && isConstant(node.argument))

const isValue = (variable) => {
  const def = variable.defs[0]
  if (!def || ['FunctionName', 'ImportBinding', 'ClassName'].includes(def.type))
    return false
  const init = initOf(variable)
  if (def.parent?.kind === 'const' && def.node.id.type === 'Identifier' && isConstant(init))
    return false
  return !(init && (['ArrowFunctionExpression', 'FunctionExpression'].includes(init.type) || hookCalledBy(init) === 'useQueryClient'))
}

const fieldsRead = (identifier) => {
  const fields = []
  for (let node = identifier; ;) {
    const { parent } = node
    if (['TSNonNullExpression', 'ChainExpression'].includes(parent.type)) {
      node = parent
    } else if (parent.type === 'MemberExpression' && parent.object === node && !parent.computed
      && !(parent.parent.type === 'CallExpression' && parent.parent.callee === parent)) {
      fields.push(parent.property.name)
      node = parent
    } else {
      return fields
    }
  }
}

const readOf = (ref) => ({ variable: ref.resolved, fields: fieldsRead(ref.identifier) })
const covers = (key, read) => key.variable === read.variable && key.fields.every((field, i) => read.fields[i] === field)

const queryKeyHasInputsRule = {
  meta: {
    type: 'problem',
    schema: [],
    messages: {
      missing: 'The query function reads `{{name}}`, but the query key does not have it, so two calls with another `{{name}}` share one cache entry. Put `{{name}}` in the key (docs/frontend/server-state.md, rule 3).',
    },
  },
  create(context) {
    const { sourceCode } = context
    const keyReads = (node) => {
      const reads = []
      const followed = new Set()
      const add = (ref) => {
        const variable = ref.resolved
        if (!isLocal(variable))
          return
        reads.push(readOf(ref))
        if (followed.has(variable))
          return
        followed.add(variable)
        const init = initOf(variable)
        if (init)
          referencesIn(sourceCode, init).forEach(add)
      }
      referencesIn(sourceCode, node).forEach(add)
      return reads
    }

    return {
      [`CallExpression:matches([callee.name=/^(${keyedQueryCalls})$/], [callee.property.name=/^(${keyedQueryCalls})$/]) > ObjectExpression.arguments:first-child`](options) {
        const property = (name) => options.properties.find((p) => p.type === 'Property' && !p.computed && p.key.name === name)?.value
        const queryKey = property('queryKey')
        const queryFn = property('queryFn')
        if (!queryKey || !queryFn || !['ArrowFunctionExpression', 'FunctionExpression'].includes(queryFn.type))
          return
        const inKey = keyReads(queryKey)
        const isInKey = (read) => inKey.some((key) => covers(key, read))
        const comesFromKey = (variable, path = []) => {
          const init = initOf(variable)
          if (!init || !['useMemo', 'useCallback', null].includes(hookCalledBy(init)) || path.includes(variable))
            return false
          const reads = referencesIn(sourceCode, init).filter((ref) => isLocal(ref.resolved) && isValue(ref.resolved)).map(readOf)
          return reads.length > 0 && reads.every((read) => isInKey(read) || comesFromKey(read.variable, [...path, variable]))
        }
        const reported = new Set()
        for (const ref of referencesIn(sourceCode, queryFn)) {
          const variable = ref.resolved
          if (!isLocal(variable) || within(variable.scope.block, queryFn) || !isValue(variable) || isInClientOptions(ref.identifier, queryFn))
            continue
          const read = readOf(ref)
          const name = [variable.name, ...read.fields].join('.')
          if (isInKey(read) || comesFromKey(variable) || reported.has(name))
            continue
          reported.add(name)
          context.report({ node: ref.identifier, messageId: 'missing', data: { name } })
        }
      },
    }
  },
}

const keyHasEachParamRule = {
  meta: {
    type: 'problem',
    schema: [],
    messages: {
      whole: 'Put the parameter `{{name}}` in the key whole, as an item of the array that the key factory returns, such as `[..., {{name}}]`. Do not take it apart, read a field of it, copy it, spread it, or give it to a call. A key that has only a part of it can leave out a value of the request, so two pages or two dates share one cache entry (docs/frontend/server-state.md, rule 3).',
      pattern: 'Take this parameter whole, give it a name, and put it in the key as an item of the array that the key factory returns, such as `(params: ListParams) => [..., params]`. A pattern, a default, or a rest element can leave out a value of the request, so two pages or two dates share one cache entry (docs/frontend/server-state.md, rule 3).',
    },
  },
  create(context) {
    const { sourceCode } = context
    const returnsOf = new Map()
    const unwrap = (node) => ['TSAsExpression', 'TSSatisfiesExpression'].includes(node?.type) ? unwrap(node.expression) : node
    const ownerOf = (node) => node.type.endsWith('FunctionExpression') || node.type === 'FunctionDeclaration' ? node : ownerOf(node.parent)
    return {
      ReturnStatement(node) {
        const fn = ownerOf(node.parent)
        returnsOf.set(fn, [...(returnsOf.get(fn) ?? []), unwrap(node.argument)])
      },
      ':function:exit'(fn) {
        const keys = fn.body.type === 'BlockStatement' ? returnsOf.get(fn) ?? [] : [unwrap(fn.body)]
        for (const param of fn.params) {
          if (param.type !== 'Identifier') {
            context.report({ node: param, messageId: 'pattern' })
            continue
          }
          const uses = sourceCode.getDeclaredVariables(fn).find((v) => v.identifiers.includes(param)).references.map((r) => r.identifier)
          const inEachKey = keys.length > 0 && keys.every((key) => key?.type === 'ArrayExpression' && key.elements.some((item) => uses.includes(item)))
          if (!inEachKey || !uses.every((use) => keys.includes(use.parent)))
            context.report({ node: param, messageId: 'whole', data: { name: param.name } })
        }
      },
    }
  },
}

export default {
  meta: { name: 'server-state' },
  rules: { 'no-copied-state': noCopiedStateRule, 'query-key-has-inputs': queryKeyHasInputsRule, 'key-has-each-param': keyHasEachParamRule },
}
