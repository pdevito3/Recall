# Personal access tokens

A person makes a personal access token at `/settings/personal-access-tokens` and pastes it once into a script or an MCP client. The dialog shows the value one time, with Copy, and no later page shows it. The list shows each token with its access in words, its dates, and its state, and Revoke while the Server allows it. The page of a user, `/users/<id>`, lists the tokens of that user for a caller with `read_users`, with Revoke for a caller who may revoke them.

## Sub-features

- `empty` shows `You have no personal access tokens yet. Make one for each client that you use.`.
- `form` opens the dialog `New personal access token` from `New token`, with `Name`, `Read my journal`, `Change my journal`, a checkbox for each permission of the person in words in the group `The token can also`, and `Expires` with `In 90 days, on <date>`, `On a date that I pick`, and `Never`.
- `journal` ticks `Read my journal` when `Change my journal` goes on, because the Server refuses a change with no read.
- `expiry` shows `Pick an expiry date in the future, or pick Never.` for a past `Last day`.
- `created` shows `Copy your new token` with the value of the `POST` answer, `Copy token`, which shows the toast `Token copied`, and `Done`. It has no `Close` button, and Escape or a click outside does not close it, so only `Done` does. Back, or a link out, while a create runs or the value shows asks `Leave without your token?` with `Stay` and `Leave`. A second Back while it shows leaves, and a sign-out in another tab leaves with no prompt of the browser.
- `list` shows each token as a list item named after the token, in the list `Your personal access tokens`, with `Active`, `Expired`, or `Revoked`, the journal access, the permissions in words, and the created, expiry, and last use dates.
- `revoke` asks `Revoke <name>?` from the button `Revoke <name>`. `Revoke token` sends the revoke, shows `Revoked`, and puts the focus on the heading.
- `admin-section` shows the list `Personal access tokens of <full name>` on the page of the user, with no value.

## How to get to it (user POV)

- Choose `Personal access tokens` in the sidebar.
- On the Settings page, choose `Manage personal access tokens`.
- Open the page of a user from `Users`, and find the section `Personal access tokens`.

## Driving it with browse.mjs

Preconditions:

- `doctor.sh` exits with 0.
- The database has no tenant, so `login()` makes the Owner, an Admin with each permission, and no token.

- **Empty.** Choose `Personal access tokens` in the sidebar after `waitForResponse` on `GET /api/v1/personal-access-tokens`. The empty text shows.
- **Form.** Choose `New token`. The dialog shows `See the users` and no permission code such as `manage_users`, and `Expires` reads `In 90 days, on <date>`.
- **Create.** Fill `Name` with a long word with no spaces, check `Change my journal`, and choose `Create token` after `waitForResponse` on `POST /api/v1/personal-access-tokens`. The answer is 201, `Read my journal` is checked, and the dialog shows the `value` of the answer. `Copy token` shows `Token copied`.
- **Use.** From Node, `fetch` `/api/v1/sessions` at the `web` URL with `Authorization: Bearer <value>`. The answer is 200.
- **Reload.** Choose `Done` and reload. The list item named after the `name` of the `GET` answer shows `Active`, and the page has no text `rcl_pat_`.
- **Admin section.** Open `/users/<id>` of the Owner. The list `Personal access tokens of <full name>` has the token and the button `Revoke <name>`.
- **Revoke.** Choose `Revoke <name>`, then `Revoke token` after `waitForResponse` on `POST /api/v1/personal-access-tokens/{id}/revoke`. The answer is 204, the item shows `Revoked`, the heading has the focus, and the same `fetch` gets 401.
- **Proof.** Run `node $S/scripts/browse.mjs $S/recipes/personal-access-tokens.mjs`. The captures `empty`, `form`, `created`, `list-after-reload`, `admin-section`, and `revoked` show each state.

## Gotchas

- The sweep gives `name` a long value in each answer, so the recipe finds a row by the `name` of the answer, not by the typed name.
- Only a person in a web or native session can make a token, so a second person needs a login. With closed signup, `user@example.com` gets the refusal page. The recipe shows the Admin section on the page of the Owner.
- The `fetch` from Node sends no cookie, so a 200 there proves the token and not the session of the page.
