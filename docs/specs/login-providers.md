# Spec: login providers

## 1. Scope

An Admin who signed in with a password adds an OIDC provider from the app, with no environment variable and no restart. A person links an identity at that provider to their own account, and then signs in with it. Once each tenant has an Admin who can sign in with a link, an Admin can turn password login off. Issue #151 gives the reasons, and [ADR 0025](../adr/0025-admins-set-login-providers-in-the-app.md) records the design. This is phase 2 of #84. Phase 1 is [password login](password-login.md).

A provider from the app is one more source of OIDC modes for `LoginModes`. Its login runs the same start, callback, and `Provisioner` as a provider from `Auth:Providers`, and it ends in the same web or native session. So no session type, token, or existing route changes. This spec changes four rules of phase 1, and their rows say so: PWD-1 holds while password login is On, PWD-14 gives a temporary password that only links a provider while it is Off, PWD-3 counts a provider of either source, and PWD-22 counts a password only while password login is On.

This spec leaves out:

- A link by a matching email, and an Admin who links an identity for another person. A person links only from their own session.
- A provider per tenant. Providers are install-wide, because the login page comes before the Server knows a tenant.
- SAML, social logins that are not OIDC, and a provider with no client secret.
- An iOS screen. The app shows each mode of `/auth/status` (Recall-ios#82).

## 2. Entities and states

| Entity | Root of | Fields that a user sets | States |
|---|---|---|---|
| A login provider, a row of `auth_oidc_providers` | None | ID, display name, issuer, client ID, client secret, `allow_http`, CA file path | Usable, Secret unreadable, Replaced by the environment |
| The login settings of the install, the one row of `auth_login_settings` | None | password login on or off | On, Off |
| An external login, a row of `auth_external_logins` | None | None. A link or a first login adds it. | None |

A provider row holds `id`, `display_name`, `issuer`, `client_id`, `protected_client_secret`, `allow_http`, `ca_certificate_path`, `created_at`, and `version`. The client secret is protected with Data Protection under its own purpose, and no answer returns it. The issuer is fixed after the row is added, because each link names its issuer (IDP-6).

A row is Secret unreadable when Data Protection cannot read its secret, Replaced by the environment when `Auth:Providers` has an entry with its ID or its issuer, and Usable otherwise. No state is stored, so no method moves a row into Secret unreadable or Replaced by the environment. A provider that is down is still Usable, as for a provider from the environment ([A provider that is down](../authentication.md#a-provider-that-is-down)).

The settings row holds `password_login` and `version`. A migration inserts the one row with `password_login` On, so two first writes cannot both insert.

An external login gains `display_label`, the email or name that the provider gave at the link, so the person can tell which account is linked.

| From | To | Method | Who |
|---|---|---|---|
| None | Usable | `LoginProvider.Add` | A caller with `manage_user_access` in a web or native session |
| Usable, Secret unreadable | Usable | `LoginProvider.Update` with a new secret | The same |
| Usable, Secret unreadable, Replaced by the environment | None | `LoginProvider.Delete` | The same |
| On | Off | `LoginSettings.TurnPasswordsOff` | The same |
| Off | On | `LoginSettings.TurnPasswordsOn` | The same |

A `LoginAttempt` gains a nullable `link_session_id`. It is set only on an attempt that links a provider to the person of that session (IDP-9). An attempt that links a provider after a temporary password holds the `user_id` instead, as a choose attempt of PWD-16 does (IDP-20).

## 3. Rules

| ID | Rule | Enforced by | Backstop |
|---|---|---|---|
| IDP-1 | `LoginModes` lists each entry of `Auth:Providers`, then each Usable row in the order of `created_at`. A save or a delete of a row changes the list for the next request, with no restart. A login in flight whose provider left the list ends at its callback with `invalid_response`. A callback redeems with the current mode of its ID. A rebuild does not dispose a replaced mode, because a login in flight can hold it, and its pooled connections close after their lifetime. | `LoginModes`, `LoginProviderChanged` | |
| IDP-2 | A row takes the rules of an entry of `Auth:Providers`: an ID of at most 100 letters, digits, dashes, or underscores that is not `password` and not an ID that `AuthOptions.ModeIdOf` gives an entry, an absolute issuer URL, an http issuer only with `allow_http`, a client ID and a client secret, and a CA path to a readable PEM file. A refused value gets 422 that names the field, and nothing is saved. | `LoginProvider.Add`, `LoginProvider.Update` | |
| IDP-3 | Two providers do not have the same ID or the same issuer, counting the entries of `Auth:Providers`. A row with the ID or the issuer of another provider gets 409. When an entry of `Auth:Providers` takes the ID or the issuer of a row after a restart, the entry wins, the row is Replaced by the environment, and the settings page says so. The boot refuses two entries with one issuer. | Handler check, `ConflictException` (409), `AuthOptionsValidator` | Unique indexes on `auth_oidc_providers.id` and `auth_oidc_providers.issuer` |
| IDP-4 | No answer, log, or audit entry holds a client secret. A read gives `secretState` of `readable` or `unreadable`. An update with no secret keeps the stored secret. | `LoginProviderDto`, `[Sensitive]` | |
| IDP-5 | A row whose secret Data Protection cannot read is not on the login page or in `/auth/status`, and its start gets 404. The settings page shows `Enter the client secret again`. `KeyRingWatch` checks the secrets of the rows with the API keys of the model providers, and the key ring warning names both. | `LoginProviderSecret`, `KeyRingWatch` | |
| IDP-6 | The issuer of a row cannot change. An update with another issuer gets 422. To move to another issuer, add a provider and delete the old one, and each person links again. | `LoginProvider.Update` | |
| IDP-7 | `Test` runs discovery against the issuer of the form, with the CA and `allow_http` of the form, and saves nothing. It answers with the issuer that the provider gives, or with the failure: unreachable, not a discovery document, an issuer that differs, or no `S256` PKCE. | `TestLoginProvider` | |
| IDP-8 | The settings page shows the callback URL to register at the provider, from the same origin as `/auth/status`. | `GetLoginProviderList` | |
| IDP-9 | A person in a web session starts a link with a post that holds `X-CSRF: 1`. The attempt holds the ID of that session. At the callback, the attempt's binding cookie and session must match the request. A callback in any other browser, or after that session ended, adds no link and goes to the settings page with the IDP-9 message. A link sends `prompt=login`, so the person signs in at the provider as the account they mean to link. A provider whose discovery lists `prompt_values_supported` without `login` gets 409 at the start, and no attempt is written, because it could answer with whoever is signed in there now. | `StartLink`, `OidcEndpoints` callback | |
| IDP-10 | A link starts only from a session that began at most 15 minutes ago. An older session gets 403, and the page asks the person to sign in again first. So a session cookie older than 15 minutes cannot add a way in that outlives it. | `StartLink` | |
| IDP-11 | The callback of a link adds a row for the issuer and subject to the user of the session, and goes to the settings page. It starts no new session. The settings page shows the `display_label` of each link. An identity that is linked to another user changes nothing, and the callback goes to the settings page with `link=conflict`, which shows the IDP-11 message. An identity that is already linked to this user changes nothing and succeeds. The Server never links by a matching email. An identity with no link that signs in follows PWD-4 and `Auth:OpenSignup` as today, and the page `This server does not accept new people` tells the person to sign in another way and link the provider in settings. | `LinkExternalLogin` | Primary key of `auth_external_logins` |
| IDP-12 | A person lists and removes their own links. A caller with `manage_user_access` lists and removes the links of a user of the tenant, under the rule of a role change: the caller holds each permission that the user holds. A removal of a link of the last Admin who can sign in gets 409 (IDP-14). | `GetExternalLogins`, `RemoveExternalLogin`, `LastAdmin.GuardAsync` | Exclusive tenant lock |
| IDP-13 | A turn of password login to Off gets 409 unless each live tenant has an Admin who holds each Admin permission and has a link to a Usable provider. Turning it On always succeeds. Each turn sends the version of the row, and a stale version gets 409. A turn to the state that the row has changes nothing and records nothing. Each other turn records the audit action `PasswordLoginTurnedOff` or `PasswordLoginTurnedOn` on the tenant of the Admin. | `LoginSettings.TurnPasswordsOff`, `LastAdmin.EachTenantHasALinkedAdmin` | Exclusive lock of each live tenant in the order of the tenant ID, version of `auth_login_settings` |
| IDP-14 | The last-Admin rule counts a link only while a Usable provider has its issuer, and a password only while password login is On. So a delete of a provider, a removal of a link, and each guard of PWD-22 get 409 when they would leave a live tenant with no Admin who holds each Admin permission and can sign in. The guard takes the issuers of the Usable providers from `LoginModes`. A delete of a provider and a turn of password login take the exclusive lock of each live tenant in the order of the tenant ID, and each other guard takes the lock of its tenant. | `LastAdmin.GuardAsync`, `DeleteLoginProvider` | Exclusive tenant locks |
| IDP-15 | While password login is Off, `/auth/status` and the login page list no password mode, `/auth/password/choose` and `/auth/password/change` get 404, `/auth/me` gives no `login_name`, and `/auth/password/login` takes only a temporary password (IDP-20). Sessions and personal access tokens that a password started keep working. When password login is Off and no provider is Usable, `/auth/status` gives the state `no_login_method`, and the login page says that sign-in is not available and that the operator can use the `claim-owner` file. | `LoginModes`, `PasswordEndpoints`, `IssueTemporaryPassword` | |
| IDP-16 | While password login is Off, the `claim-owner` password path still works and does not turn passwords On. So an operator whose providers all fail gets back in from the disk. | `PasswordLogin.ClaimOwnerAsync` | |
| IDP-17 | PWD-2 and PWD-3 count a provider of either source. With an entry of `Auth:Providers` or a Usable row, the Server makes no setup code, and the setup page and the setup post get 404. So a row that outlives the last tenant keeps the setup closed. | `SetupCodeAtBoot`, `PasswordEndpoints` | |
| IDP-18 | An add, an update, and a delete of a provider record the audit actions `LoginProviderAdded`, `LoginProviderUpdated`, and `LoginProviderDeleted`, with no secret. A link and a removal record `ExternalLoginLinked` and `ExternalLoginRemoved` on the User. | Audit log | |
| IDP-19 | A PAT, an agent, and a provider token get 403 on each route of this spec. | `PermissionGuard.RequireSignedInPersonAsync` | |
| IDP-20 | While password login is Off, an issue of a temporary password still works. While a live user holds a temporary password that is not Stopped, the login page shows `I have a temporary password`, which opens the password form, so a single provider still starts with no click when no one waits to link. A Chosen or Stopped password there gets the 401 and the message of PWD-6, so the form tells nothing about the account. A right temporary password starts no session and no app code. It writes a login attempt with the `user_id`, the time of the temporary password, and the web or native client of the form, and starts the login at a Usable provider with `prompt=login`. With several providers, the page links to each, and a provider that does not offer `prompt=login` is left out, as in IDP-9. The callback adds the link under IDP-11, deletes the temporary credential in the same save, and then starts the session or the app code through `LoginCompletion`. That save records `ExternalLoginLinked` and `TemporaryPasswordEnded` on the User, with the user of the attempt as the actor. A callback whose identity is linked to another user adds no link, starts no session, and answers a page with status 409 and the IDP-11 message. That callback deletes nothing, so the temporary password still works until it expires. A temporary password that changed or expired after the attempt makes the callback add nothing, and it gets the `Refused` page. With no Usable provider, the form gets the IDP-15 message. | `PasswordLogin`, `LinkExternalLogin`, `LoginCompletion` | Version of `auth_password_credentials` |

## 4. Permissions

None. Each route uses `manage_user_access`, or the session of the person. The permission is held per tenant and the providers are install-wide, so an Admin of any tenant can change them (ADR 0025).

## 5. Endpoints and tools

| Feature | HTTP | MCP tool | Permission |
|---|---|---|---|
| `GetLoginProviderList` | `GET /api/v1/login-providers` | None | `manage_user_access`, in a web or native session |
| `AddLoginProvider` | `POST /api/v1/login-providers` | None | The same |
| `UpdateLoginProvider` | `PUT /api/v1/login-providers/{id}` | None | The same |
| `DeleteLoginProvider` | `DELETE /api/v1/login-providers/{id}` | None | The same |
| `TestLoginProvider` | `POST /api/v1/login-providers/test` | None | The same |
| `GetLoginSettings` | `GET /api/v1/login-settings` | None | The same |
| `UpdateLoginSettings` | `PUT /api/v1/login-settings` | None | The same |
| `GetExternalLogins` | `GET /api/v1/me/external-logins`, `GET /api/v1/users/{id}/external-logins` | None | A web or native session, or `manage_user_access` |
| `RemoveExternalLogin` | `DELETE /api/v1/me/external-logins`, `DELETE /api/v1/users/{id}/external-logins`, with the issuer and the subject | None | A web or native session, or `manage_user_access` |
| `StartLink` | `POST /auth/oidc/{id}/link` | None | A web session, `X-CSRF: 1` |

`StartLink` is an `/auth/*` route, so it is not in the OpenAPI document. It answers with the URL of the provider, and the SPA navigates there.

## 6. Messages

| Rule | Status | Message |
|---|---|---|
| IDP-2 | 422 | Give an issuer URL that starts with https://, or turn on Allow HTTP. |
| IDP-2 | 422 | The server cannot read a certificate at this path. |
| IDP-2 | 422 | Give an issuer URL with no query and no fragment. |
| IDP-3 | 409 | Another provider uses this ID. Pick another. |
| IDP-3 | 409 | Another provider uses this issuer. |
| IDP-5 | none | The server cannot read this client secret. Enter it again. |
| IDP-6 | 422 | The issuer of a provider cannot change. Add a new provider instead. |
| IDP-7 | none | The server cannot reach this issuer. |
| IDP-7 | none | This URL gives no OpenID Connect discovery document. |
| IDP-7 | none | The provider names another issuer: {issuer}. |
| IDP-7 | none | This provider does not offer S256 PKCE, which Recall needs. |
| IDP-9 | none | Recall did not link the provider, because the link started in another browser or session. Start it again from settings. |
| IDP-9 | 409 | Recall cannot link {provider}, because it does not let Recall ask you to sign in again there. |
| IDP-10 | 403 | Sign in again, then link the provider within 15 minutes. The problem type is `urn:recall:problem:sign-in-again-to-link`. |
| IDP-11 | none | This account at the provider is linked to another person in Recall. |
| IDP-13 | 409 | Each tenant needs an Admin who can sign in with a provider before password login turns off. |
| IDP-12, IDP-14 | 409 | `LastAdmin.RefusedMessage`, as for PWD-22 |
| IDP-15 | 503 | Sign-in is not available. The operator can use the claim-owner file to get back in. |
| IDP-20 | 409 | This account at the provider is linked to another person in Recall. |
| IDP-20 | 503 | No provider answered. Wait a few minutes, then try again. |

## 7. Time zones

| Value | Stored as | Shown in the zone of |
|---|---|---|
| `auth_oidc_providers.created_at`, `auth_external_logins.created_at`, `last_login_at` | UTC instant | The browser |

## 8. Open decisions

| Decision | Who decides | Rules that wait |
|---|---|---|
| While password login is Off, a temporary password only links a provider, and the person gets a session after the link (IDP-20). The other choice, an Admin who turns passwords On to add a person, lets each old password in while it is On. | Decided by the operator | None |
| An Admin of any tenant can change the install-wide providers. The operator accepted this, because an install has one tenant in practice. | Decided by the operator | None |
