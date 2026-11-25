import { selectorRule } from './selector-rule.js'

export const selectorMessages = {
  app: 'Do not give a selector string to an action. Find the element with getByRole or getByLabel in the *.locators.ts file of the feature, and call the action on that Locator.',
  stack: 'Do not give a selector string to an action. Put a selector of the login page of an IdP in its e2e/idps/*.json file and call the action on page.locator(idp.<name>Selector). For the app or Swagger UI, find the element with getByRole or getByLabel and call the action on that Locator.',
}

// The actions of a Playwright Page or Frame that take a selector first. A Locator has the same actions with no selector,
// so the selector form has one more argument before the options object. Oxlint has no types, so the rule reads the
// arguments.
const plainActions = new Set(['click', 'dblclick', 'check', 'uncheck', 'hover', 'tap', 'focus', 'textContent', 'innerText', 'innerHTML', 'inputValue', 'isChecked', 'isDisabled', 'isEditable', 'isEnabled', 'isHidden', 'isVisible'])
const valueActions = new Set(['fill', 'type', 'press', 'setChecked', 'selectOption', 'setInputFiles', 'dispatchEvent', 'getAttribute'])
const selectorOnlyActions = new Set(['waitForSelector', 'dragAndDrop', '$', '$$', '$eval', '$$eval'])

const coordinateOrKeyReceivers = new Set(['mouse', 'keyboard', 'touchscreen'])
const receiverName = (receiver) => receiver.type === 'MemberExpression' && !receiver.computed ? receiver.property.name : receiver.name

const isSelectorArgument = (node) => node !== undefined && node.type !== 'ObjectExpression'

const takesSelector = (action, args) =>
  selectorOnlyActions.has(action)
  || (plainActions.has(action) && isSelectorArgument(args[0]))
  || (valueActions.has(action) && isSelectorArgument(args[1]))

const noSelectorStringRule = {
  meta: {
    type: 'problem',
    schema: [{ enum: Object.keys(selectorMessages) }],
    messages: selectorMessages,
  },
  create(context) {
    const messageId = context.options[0] ?? 'app'
    return {
      'CallExpression > MemberExpression.callee'(callee) {
        if (!callee.computed && !coordinateOrKeyReceivers.has(receiverName(callee.object)) && takesSelector(callee.property.name, callee.parent.arguments))
          context.report({ node: callee.parent, messageId })
      },
    }
  },
}

const positionMessage = 'Do not find an element by its position. Give the element a role and a name in the app, and find it by them in the *.locators.ts file of the feature.'

const playwrightTestMessage = 'Import test and expect from @tests/support/fixtures. Its test fails on a request that no test mocked.'

export const testOnlyMessage = 'Remove test.only. CI runs each test.'

export default {
  meta: { name: 'playwright' },
  rules: {
    'no-selector-string': noSelectorStringRule,
    'no-test-only': selectorRule([
      { selector: "MemberExpression[property.name='only']:matches([object.name='test'], [object.object.name='test'])", message: testOnlyMessage },
    ]),
    'no-position': selectorRule([
      { selector: "CallExpression[callee.property.name='nth']", message: positionMessage },
      { selector: 'CallExpression[arguments.length=0][callee.property.name=/^(first|last)$/]', message: positionMessage },
    ]),
    'no-wait-for-timeout': selectorRule([
      { selector: "CallExpression[callee.property.name='waitForTimeout']", message: 'Do not wait for a time. In the *.tasks.ts file of the feature, wait for the response of the request, or let an expect retry until the state shows.' },
    ]),
    'spec-no-locator': selectorRule([
      { selector: 'CallExpression[callee.property.name=/^(locator|frameLocator|getBy(Role|Text|Label|Placeholder|AltText|Title|TestId))$/]', message: 'A spec does not know a selector. Move the selector to the *.locators.ts file of the feature, and read it through the page object of the feature.' },
    ]),
    'spec-no-wait': selectorRule([
      { selector: "CallExpression[callee.object.name='page'][callee.property.name=/^waitFor/]", message: 'A spec does not know a wait. Move the wait to a task in the *.tasks.ts file of the feature, and arm it before the action.' },
    ]),
    'spec-no-playwright-test': selectorRule([
      { selector: "ImportDeclaration[source.value='@playwright/test'] > :matches(ImportSpecifier[imported.name=/^(test|expect)$/], ImportNamespaceSpecifier)", message: playwrightTestMessage },
      { selector: "ExportNamedDeclaration[source.value='@playwright/test'] > ExportSpecifier[local.name=/^(test|expect)$/], ExportAllDeclaration[source.value='@playwright/test']", message: playwrightTestMessage },
    ]),
    'spec-no-timer': selectorRule([
      { selector: "CallExpression[callee.name='setTimeout']", message: 'A spec does not wait for a time. Wait for a response or a state in a task in the *.tasks.ts file of the feature.' },
    ]),
  },
}
