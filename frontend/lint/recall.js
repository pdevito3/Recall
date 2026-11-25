import { builtinModules } from 'node:module'

import { selectorRule } from './selector-rule.js'

const builtins = new Set(builtinModules)
const groups = ['a Node module', 'a package', 'an `@/` import', 'a relative import']
const groupOf = (source) => {
  if (source.startsWith('node:') || builtins.has(source.split('/')[0]))
    return 0
  if (source.startsWith('.'))
    return 3
  return source.startsWith('@/') || source.startsWith('@tests/') ? 2 : 1
}

const isSideEffectImport = (node) => node.specifiers.length === 0

const importOrderRule = {
  meta: {
    type: 'suggestion',
    schema: [],
    messages: { order: 'Put the import of `{{source}}` ({{group}}) before the import of `{{before}}` ({{beforeGroup}}). The order is Node modules, packages, `@/` imports, then relative imports.' },
  },
  create(context) {
    let last = null
    return {
      ImportDeclaration(node) {
        if (isSideEffectImport(node))
          return
        const group = groupOf(node.source.value)
        if (last && group < last.group)
          context.report({ node, messageId: 'order', data: { source: node.source.value, group: groups[group], before: last.source, beforeGroup: groups[last.group] } })
        else
          last = { group, source: node.source.value }
      },
    }
  },
}

const codeToWordsMessage = 'Do not make words from a code (docs/messages.md, rule 1). Name a time zone with timeZoneLabel(zone) from @/lib/zoned-time. Give another code a property or a map with its words.'
export const codeToWordsRules = [
  {
    selector: "CallExpression[callee.property.name=/^replace(All)?$/]:matches([arguments.0.value='_'], [arguments.0.regex.pattern=/_/]):matches([arguments.1.value=' '], [arguments.1.quasis.0.value.cooked=' '][arguments.1.expressions.length=0])",
    message: codeToWordsMessage,
  },
  {
    selector: "CallExpression[callee.property.name='join'][arguments.0.value=' '][callee.object.callee.property.name='split'][callee.object.arguments.0.value='_']",
    message: codeToWordsMessage,
  },
]

export const browserZoneRules = [
  {
    selector: 'CallExpression[callee.property.name=/^toLocale(Date|Time)?String$/]',
    message: 'This uses the zone of the browser. Use formatInstant (a time of a place) or formatDate (a date with no time) from @/lib/zoned-time. For a number, use Intl.NumberFormat.',
  },
  {
    selector: "MemberExpression[object.name='Intl'][property.name='DateTimeFormat']",
    message: 'Use formatInstant (a time of a place) or formatDate (a date with no time) from @/lib/zoned-time. They set the zone.',
  },
  {
    selector: 'CallExpression[arguments.length=0][callee.property.name=/^get(FullYear|Month|Date|Day|Hours|Minutes|Seconds|TimezoneOffset)$/]',
    message: 'This reads the date or the time in the zone of the browser. Use wallClockAt(instant, zone) or todayAt(zone, now) from @/lib/zoned-time.',
  },
  {
    selector: 'CallExpression[callee.property.name=/^set(FullYear|Month|Date|Hours|Minutes|Seconds)$/]',
    message: 'This sets the date or the time in the zone of the browser. Use instantAt(wallClock, zone) from @/lib/zoned-time.',
  },
  {
    selector: "NewExpression[callee.name='Date'][arguments.length>1]",
    message: 'This makes a time in the zone of the browser. Use new CalendarDate(...) for a date, or instantAt(wallClock, zone) from @/lib/zoned-time for a time of a place.',
  },
  {
    selector: "ImportDeclaration[source.value='@internationalized/date'] > ImportSpecifier[imported.name=/^(getLocalTimeZone|today|now|DateFormatter)$/]",
    message: 'Use todayAt(zone, now) and browserTimeZone() from @/lib/zoned-time, and its formatters. Take the zone of the place, not of the browser.',
  },
  {
    selector: "ImportDeclaration[source.value=/^(react-aria|@react-aria\\u002Fi18n)(\\u002F|$)/] > ImportSpecifier[imported.name=/^(useDateFormatter|DateFormatter)$/]",
    message: 'The React Aria date formatter formats in the zone of the browser unless it is given one. Use formatInstant (a time of a place) or formatDate (a date with no time) from @/lib/zoned-time.',
  },
  {
    selector: 'ImportDeclaration[source.value=/^date-fns/]',
    message: 'date-fns formats in the zone of the browser. Use @/lib/zoned-time.',
  },
]

const wayOutMessage = 'Leave the app with leaveTheApp from @/domain/auth/apis/leave-the-app. A navigation or a reload here loses the words that only the memory of the tab holds (docs/frontend.md, section "Leaving the app").'
export const wayOutRules = [
  {
    selector: "MemberExpression[property.name=/^(reload|assign|replace)$/]:matches([object.name='location'], [object.property.name='location'])",
    message: wayOutMessage,
  },
  {
    selector: "AssignmentExpression > MemberExpression.left:matches([object.name='location'], [object.property.name='location'], [property.name='location'][object.name=/^(window|document|globalThis|self)$/])",
    message: wayOutMessage,
  },
  {
    selector: "AssignmentExpression > Identifier.left[name='location']",
    message: wayOutMessage,
  },
]
const loginPageMessage = 'Name a login page only in leave-the-app.ts, and leave with leaveTheApp (docs/frontend.md, section "Leaving the app").'
export const loginPageRules = [
  {
    selector: 'Literal[value=/^\\/auth\\/login/], TemplateElement[value.raw=/^\\/auth\\/login/]',
    message: loginPageMessage,
  },
]

export default {
  meta: { name: 'recall' },
  rules: {
    'import-order': importOrderRule,
    'no-browser-zone': selectorRule(browserZoneRules),
    'no-words-from-code': selectorRule(codeToWordsRules),
    'no-way-out': selectorRule(wayOutRules),
    'no-login-page': selectorRule(loginPageRules),
  },
}
