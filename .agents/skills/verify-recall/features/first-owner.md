# First owner

A person does not start a tenant on a page anymore. The first login of a new database makes the tenant `Recall` and makes that person its Owner, an Admin of the tenant. When the database has a tenant and `Auth:OpenSignup` is off, each other person who signs in gets the refusal page of the Server, so a signed-in person with no user record does not exist.

## Sub-features

- `first-owner` makes the tenant `Recall` and an Admin at the first login of a new database. The home page shows no notice.
- `signup-closed` shows the page `This server does not accept new people` with status 403 to a second person. That person has no session.
- `initiate-refused` answers `POST /api/v1/users/initiate` with a refusal, because the Server creates each first user at login.

## How to get to it (user POV)

- Log in on a new database. You are the Owner.
- Log in as another user of the IdP after that. You see the refusal page.

## Driving it with browse.mjs

Preconditions:

- `doctor.sh` exits with 0.
- The database has no tenant. This is true on a new stack, and `browse.mjs` restores it after each recipe.
- The IdP is ZeroFactor, because the recipe clicks the second user on its page.

- **First owner.** Run `login('admin@example.com')`. `fetch('/api/v1/users/me')` in the page gives 200 with the `name` of the login. `page.getByRole('region', { name: 'Membership check' })` has count 0.
- **Permissions.** `fetch('/api/v1/users')` in the page gives 200 with one row, `roles` `["Admin"]`, and the `identifier` of `/api/v1/users/me`.
- **Second person.** Run `logout()` and `context.clearCookies()`, so that the session of the IdP ends too. Open `/`, then click `[data-testid="user-user@example.com"]`. The response of `/auth/oidc/callback` is 403. The heading is `This server does not accept new people`, and the title is `This server does not accept new people - Recall`. The link `Use another account` starts a new login with `prompt=login`. `/auth/me` gives 204.
- **Proof.** Run `node $S/scripts/browse.mjs $S/recipes/first-owner.mjs`. The captures `home-of-owner` and `signup-closed` show the two states.

## Gotchas

- The first login wins the tenant. The restore of `browse.mjs` deletes that tenant, so the next run starts with no tenant. With `--keep-data`, the first user of the next run is not the first login.
- `AuthProvider=None` with `Auth:OpenSignup=true` lets a second person join the oldest tenant as a User. The recipe does not cover that shape.
- A recipe that keeps the session of the IdP signs in the same person again with no click. Clear the cookies of the context to reach the user list of ZeroFactor.
