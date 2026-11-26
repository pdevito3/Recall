# Users

A tenant admin sees the users of the tenant at `/users`, opens a user, adds and removes a role of the user, and deletes the user. The detail page shows a button only for each action that the Server allows the caller. A member with no `read_users` permission sees the refusal of the Server on the list.

## Sub-features

- `list` shows the table `Users` with the name, the email, and the roles of each user of the tenant, and the pager `Pages of users`.
- `detail` shows the name of the user as the level 1 heading, with the email, the username, the roles, and the permissions.
- `actions` shows the group `Actions for <full name>` with `Add role`, `Remove role`, and `Delete` for an admin. It shows no button for an action that the page cannot send yet, such as an edit of the user.
- `roles` adds a role with `POST /api/v1/users/{id}/roles` and removes one with `DELETE /api/v1/users/{id}/roles/{role}`. The dialog offers only the roles that the user does not have. The page shows the new roles and permissions and moves the focus to the heading.
- `delete` asks for a confirmation in the dialog `Delete <full name>?`, sends `DELETE /api/v1/users/{id}`, shows the toast `User deleted` one time, and goes to the list with the focus on its heading.
- `refused` shows `The users did not load.` with the refusal of the Server for a member with no `read_users`.

## How to get to it (user POV)

- Choose `Users` in the sidebar.
- Choose the name of a user in the list.
- Open `/users` or `/users/<id>` directly.

## Driving it with browse.mjs

Preconditions:

- `doctor.sh` exits with 0.
- The database has no tenant. This is true on a new stack. The first login makes the user of `login()` the Owner, an Admin of the tenant `Recall`.
- The SPA has no page to add a user yet. The recipe adds a second user with `fetch('/api/v1/users')` in the page, with the `X-CSRF: 1` header. The Server makes the identifier of that user its ID.

- **List.** Choose `Users` in the sidebar after `waitForResponse` on `GET /api/v1/users`. Each row of the table has the name, the email, and the roles of its row in the response.
- **Detail.** Choose the name of the second user. The heading is the `fullName` of the user, and the group `Actions for <fullName>` has the buttons `Add role`, `Remove role`, and `Delete`, in this order.
- **Roles.** The colleague starts as an Auditor. Choose `Add role`, pick `User manager` in `Role`, and confirm after `waitForResponse` on `POST /api/v1/users/{id}/roles`. The response is 200 with both roles, and the details show both. Then remove `Auditor` after `waitForResponse` on `DELETE /api/v1/users/{id}/roles/Auditor`: `read_users` stays, because User manager gives it. The heading has the focus after each change.
- **Delete.** Choose `Delete`, then `Delete user` in the dialog after `waitForResponse` on `DELETE /api/v1/users/{id}`. The response is 204. The toast `User deleted` has count 1. The heading `Users` has the focus, and the list has no row for the user.
- **Proof.** Run `node $S/scripts/browse.mjs $S/recipes/users.mjs`. The captures `list`, `detail`, `add-role-dialog`, `roles-added`, `role-removed`, `delete-dialog`, and `after-delete` show each state.

## Gotchas

- The queries retry one time. For a refused list, the toast shows first, and `The users did not load.` shows about 1 s later. Wait for the text, not for the toast.
- A user that the API adds has no login. It exists only as a row, so the recipe does not sign in as the colleague.
