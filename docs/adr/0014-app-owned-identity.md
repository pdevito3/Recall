# Recall owns its identity and brings an OIDC provider only for login

An identity provider proves who a person is one time. Then Recall issues its own auth session, and the rest of the app reads only that auth session. One `AppSession` scheme reads a bearer token or a cookie and gives the internal user ID. The tables are `auth_external_logins`, `auth_sessions`, `auth_login_attempts`, and `auth_app_codes`. `ILoginMode` and `/auth/status` describe the ways to log in. The plan's R3 builds this design, which the plan calls S2.

A homelab app cannot depend on the token format of one provider, and a later local login must not need an internal issuer.

## Evidence

The prototype is the local branch `proto/auth-s2`, head `495bf066462a7257546aa71d10e92cac4e7f6060`, which is not in this repository. The results are in [`docs/evidence/auth/`](../evidence/auth/).

- [`s1-kill.txt`](../evidence/auth/s1-kill.txt) shows why a forwarded provider token fails. The stock Server rejects an opaque Authelia token, and it rejects a Keycloak token with no `aud`. It accepts the Keycloak token only after the audience setting changes. After a Server restart while the provider is down, it rejects the token that it accepted before.
- [`local-commit-stat.txt`](../evidence/auth/local-commit-stat.txt) shows that local login is additive. Commit `988cb19a` adds one file of 98 lines and three lines in `RecallAuth.cs`. It adds three endpoints (`/auth/local/setup`, `/auth/local/login`, and `/auth/local/password`) and one table ([`schema-diff.txt`](../evidence/auth/schema-diff.txt)). It changes no auth session type, no authorization code, and no existing endpoint. The real build also adds a migration and a snapshot edit.
- [`proof-c.txt`](../evidence/auth/proof-c.txt) shows that password login works in a real browser and from an app client, and that it still works with each provider stopped. Its section E shows the setup of a new install with no identity provider.

Pocket ID and Authentik did not run with this flow.

## Considered options

- **Keep the BFF and forward the provider token (S1).** We did not choose this option, for the reasons in [`s1-kill.txt`](../evidence/auth/s1-kill.txt). Local login would also need an internal issuer.

## Consequences

- A person with no identity provider cannot use Recall until the next program adds local login. OIDC-only is a scope choice and not a limit of the design, because setup with no identity provider worked in the prototype. This is the largest gap for adoption, and the plan accepts it.
- Recall holds tokens for a private journal. This design is hard to reverse, so R3 runs the architect and interrogate skills and a security review before merge.
- The internal user ID, and not the `sub` of a provider, identifies a person in the journal.

## Decisions of the build

R3 made these decisions when it built the design. [docs/authentication.md](../authentication.md) gives the contract.

- **The first login makes the Owner.** Only a tenant row decides it, and the database admits one first tenant, so no lock exists. Later identities get a refusal unless `Auth:OpenSignup` is on. So a stranger cannot take an install that has an Owner.
- **A file on the disk recovers a lost Owner.** A changed issuer or provider must not cost the journal, and it must not let a stranger in. So the operator proves control of the disk with a one-shot file, `claim-owner`. We chose this over a setup token, because a person who lost a login still has the disk. The file works for 15 minutes, and the first login after it removes it, also a login with a link. A forgotten file then cannot wait for a stranger. The first login can still be a stranger inside the 15 minutes. We accept that, because a stranger must first pass the provider, and the operator can limit the provider while the claim runs.
- **A tenant keeps one Admin who can sign in.** With closed signup, an install with no such Admin needs a disk claim to recover, and that is too much for one click in the app. So a delete, a demotion, or the removal of `manage_user_access` gets 409 when it would leave the tenant with no Admin who holds that permission, or would remove the last such Admin with a link. An Admin with no link can go when another such Admin stays, because its loss takes no way in from anyone. The rule reads a set of rows, so these three commands hold the exclusive lock of the tenant.
- **No provider is a valid state.** The Server boots, runs its migrations, and says what to set. A typo in a provider setting still stops the boot, because it is not the same as no provider.
- **One install answers at each origin, with no origin setting.** The Server takes the origin from each request and never from `X-Forwarded-Host`. This is the only shape that lets `http://LAN-IP` and a TLS name work at the same time. The cost is that the provider's exact redirect match, and `AllowedHosts` when it is set, are the gate on a forged `Host`.
- **A cookie and a bearer token are one kind of credential.** Both are opaque values whose hash is on one session row. So one lookup serves each request and a revocation applies at once. The cookie has the name `recall` on http and `__Host-recall` on TLS, because a `__Host-` cookie needs TLS.
- **A retry is derived, and nothing about it is stored.** A refresh with a retry key computes its pair with an HMAC of the old token and the key, so each copy of one request gets the same pair and writes nothing. An older token with no key is a replay and ends the session. Three independent runs found that a stored retry answer lets a delayed copy strand the phone.
- **A native sign-in needs one tap at the Server.** A provider can sign a person in with no click, so a page of the Server stands before the code reaches the app (RFC 8252 section 8.6). The tap happens at sign-in and never at capture.
- **A session outlives the provider.** No path of a session calls a provider, so capture works with the provider down. For the same reason a provider-side disable does not end a session, and back-channel logout is gone. `revoke-others` and a delete of the user are the remedies.
- **A native refresh token has no absolute limit.** A phone that captures after a long gap must not ask for a sign-in at that moment.
- **Files are owner-only, and modes never stop a boot.** The Server sets the umask to `0077` and tightens the data root at startup. On a NAS volume that ignores a mode, it logs one warning and goes on, because a refused boot there is worse than a group-readable file on a volume that the NAS owner controls.
- **The BFF left the running stack in R3, and its code leaves in R4.** The Server answers `/bff/login`, `/bff/user`, and `/bff/logout`, so the SPA has no login change in R3. This split keeps the size of each change in check.
- **Two uses of provider tokens stop.** A provider token names no user of the app, so a Swagger UI login reads no tenant data, and a delegated agent becomes an unknown client. Machine agents do not change. R4 removes the JWT code that has no caller.
