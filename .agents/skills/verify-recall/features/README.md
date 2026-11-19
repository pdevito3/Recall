# Recall verification map

This directory is the maintained source for the proof of user-facing behavior in the Recall SPA. Read this index before you drive the app. Then use the matching feature file as the recipe.

## Baseline preconditions

- `scripts/launch.sh` started an isolated stack for this checkout, and `scripts/doctor.sh` exits with 0.
- The browser is a new Chromium context from `scripts/browse.mjs`. Nobody is logged in. `localStorage` is empty, so the theme is `system`.
- The database has the rows of a new stack. `browse.mjs` puts back each row that a recipe writes, so each recipe starts from the same rows. Each recipe that logs in writes rows, because the first login makes the Owner. A login writes an auth session row, and the restore deletes it.
- The IdP is ZeroFactor, unless `launch.sh` got `--auth-provider`. The ZeroFactor users are `admin@example.com` (name `Admin User`) and `user@example.com` (name `Test User`).
- Never drive a stack that the current verification run did not start.

## Driving conventions

- Run each recipe with `node .agents/skills/verify-recall/scripts/browse.mjs .agents/skills/verify-recall/recipes/<feature>.mjs`.
- Each recipe starts from the baseline state. A recipe that needs a login calls `login()` itself and uses the `name` that it returns. Do not write a fixed user name in a recipe, because each IdP has other users.
- Use ARIA roles and accessible names. Use CSS only where a feature file names it.
- Copy the quoted names and commands exactly.
- Assert that the screen matches the API response of the same run, never a fixed value.

## Proof and skip reporting

- Capture the user action and the resulting state, not only the last screen.
- UI proof is a screenshot and an ARIA snapshot from `capture()`, with the URL in the `.aria.txt` file.
- Proof of a side effect is a second, read-only view: the API response, the session of the Server, or `localStorage`.
- Record the feature ID and the entry point for each artifact. The `steps.log` step names do this.
- If a path is not reachable, report the command and the precondition that failed. For example, the Duende demo IdP is down.
- Do not report an entry point as verified when you used a different entry point.

## Feature entry contract

Each feature file starts with an H1 title and one paragraph about the user-visible behavior. It then has exactly four H2 sections in this order:

1. `Sub-features` gives short IDs, one line for each behavior.
2. `How to get to it (user POV)` gives each user entry point.
3. `Driving it with browse.mjs` starts with `Preconditions:`. Then labeled bullets give each user action, the exact Playwright call, and the observable result.
4. `Gotchas` gives the traps that can waste or invalidate a verification run.

Keep implementation details out of the map. Name only user paths, stable handles, required state, commands, and observable proof.

## Features

- [Login and logout](./login-logout.md) covers the Server login through the IdP of the stack, the session, the name of the user, and logout.
- [Navigation and theme](./navigation-and-theme.md) covers the sidebar links, the breadcrumb, sidebar collapse, and the light/dark/system theme.
- [First owner](./first-owner.md) covers the first login, which makes the Owner and its tenant, and the refusal of a second person when signup is closed.
- [Journal](./journal.md) covers capture on `/`, autosave, two tabs, two devices, the history, the delete, a phone width, the keyboard, a lost connection, and the file store notice.
- [Mic](./mic.md) covers the mic card on `/`: one tap records into a new entry, and the audio outlives a closed tab, a lost network, and a stopped Server until its words are in the journal.
- [Job dashboard](./job-dashboard.md) covers `/backwave`: the BackWave console of the Owner, the finished health job, and the 403 for each other caller.
- [Users](./users.md) covers `/users` and `/users/<id>`: the list of the users of the tenant, the actions from `allowedActions`, the role change, the delete, and the refusal for a member.
- [Personal access tokens](./personal-access-tokens.md) covers `/settings/personal-access-tokens` and the section on `/users/<id>`: make a token, see its value once, use it as a bearer, and revoke it.
- [Model providers and Cleanup](./model-providers.md) covers `/model-providers`: connect, test, pick a model, and make it active with a local fake, a second provider with a model that does not take the place of the active provider, a wrong key, and a Cleanup that runs after the page closes.

## Not mapped yet
- Swagger UI on the server (`/swagger`). The Playwright spec `frontend/e2e/auth-flows.spec.ts` covers it.
- The Users and Tenants JSON API. It has no UI.
