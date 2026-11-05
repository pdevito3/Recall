# Recall owns its identity and brings an OIDC provider only for login

An identity provider proves who a person is one time. Then Recall issues its own auth session, and the rest of the app reads only that auth session. One `AppSession` scheme reads a bearer token or a cookie and gives the internal user ID. The tables are `auth_external_logins`, `auth_sessions`, `auth_login_attempts`, and `auth_app_codes`. `ILoginMode` and `/auth/status` describe the ways to log in. The plan's R3 builds this design, which the plan calls S2.

A homelab app cannot depend on the token format of one provider, and a later local login must not need an internal issuer.

## Evidence

The prototype is `~/.claude/orchestrate/recall/prototypes/auth/template` on branch `proto/auth-s2`, head `495bf066462a7257546aa71d10e92cac4e7f6060`. The results are in `~/.claude/orchestrate/recall/prototypes/auth/evidence/`.

- `s1-kill.txt` shows why a forwarded provider token fails. The stock Server rejects an opaque Authelia token, and it rejects a Keycloak token with no `aud`. It accepts the Keycloak token only after the audience setting changes. After a Server restart while the provider is down, it rejects the token that it accepted before.
- `local-commit-stat.txt` shows that local login is additive. Commit `988cb19a` adds one file of 98 lines and three lines in `RecallAuth.cs`. It changes no auth session type, no authorization code, and no endpoint. The real build also adds a migration and a snapshot edit.
- `proof-c.txt` shows that password login works in a real browser and from an app client, and that it still works with each provider stopped.

Pocket ID and Authentik did not run with this flow.

## Considered options

- **Keep the BFF and forward the provider token (S1).** We did not choose this option, for the reasons in `s1-kill.txt`. Local login would also need an internal issuer.

## Consequences

- A person with no identity provider cannot use Recall until the next program adds local login. This is the largest gap for adoption, and the plan accepts it.
- Recall holds tokens for a private journal. This design is hard to reverse, so R3 runs the architect and interrogate skills and a security review before merge.
- The internal user ID, and not the `sub` of a provider, identifies a person in the journal.
