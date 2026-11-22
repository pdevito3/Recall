# Spec: password login

## 1. Scope

A person signs in to Recall with a login name and a password, and the install needs no identity provider. The first person sets up the install with a code from the Server log. An Admin adds each other person with a temporary password, and that person picks their own password at the first sign-in. Issue #84 gives the reasons, and [ADR 0024](../adr/0024-recall-signs-people-in-with-its-own-passwords.md) records the design. This closes #24 for a person with a password.

Password login is one more login mode of [ADR 0014](../adr/0014-app-owned-identity.md). It ends in the same web or native session as an OIDC login, so no session type, token, or existing route changes.

This spec leaves out:

- MFA, passkeys, and email. A person who wants them uses an OIDC provider.
- OIDC providers that an Admin sets in the app, the link of an OIDC identity to a password user, and a switch that turns password login off. Phase 2 builds them.
- A change of a login name after it is set.
- A check of a password against an online breach service.

## 2. Entities and states

| Entity | Root of | Fields that a user sets | States |
|---|---|---|---|
| A password credential, a row of `auth_password_credentials` | None | login name (an Admin), password (its person, or an Admin through a temporary password) | Temporary, Chosen, Stopped |
| The setup code, held in memory | None | None. The Server makes it. | None |

The row holds `user_id`, `login_name`, `password_hash`, `changed_at`, `failed_count`, and `temporary_until`. `login_name` holds the normalized form. The table is `[NotAudited]`, and the audit log records each change as an action on the User.

A row is Temporary when `temporary_until` is set, Stopped when `failed_count` is 100 or more, and Chosen otherwise. Expired is not a stored state. A Temporary row whose `temporary_until` has passed is expired.

| From | To | Method | Who |
|---|---|---|---|
| None, Temporary, Chosen, Stopped | Temporary | `PasswordCredential.IssueTemporary` | A caller with `manage_user_access` in a web or native session |
| Temporary | Chosen | `PasswordCredential.Choose` | Its person, after a sign-in with the temporary password |
| Chosen | Chosen | `PasswordCredential.Change` | Its person, in a web or native session, with the current password |
| Chosen | Stopped | `PasswordCredential.NoteFailure` | The 100th wrong password in a row |
| None, Temporary, Chosen, Stopped | Chosen | `OwnerClaim` | The operator, through the `claim-owner` file |

A `LoginAttempt` gains a nullable `user_id`. It is set only on an attempt that waits for the person to choose a password (PWD-16).

## 3. Rules

| ID | Rule | Enforced by | Backstop |
|---|---|---|---|
| PWD-1 (later) | `/auth/status` lists one mode of kind `password` with the state `ready` when the install has no OIDC provider, or when a live user holds a password credential. Then the login page shows the password form and a button for each OIDC mode. An install with providers and no password credential shows no password mode, so a single provider still starts with no click. An install with no provider always has the password mode, so `no_login_method` no longer occurs. | `PasswordLoginMode`, `LoginModes` | |
| PWD-2 (later) | While no live tenant exists, the Server makes a setup code at boot from 20 random base32 characters, holds it in memory, and writes it once to the console and to `/data/logs`. A restart makes a new code, and the old one stops working. `/auth/status` gives `setupRequired: true`. | `SetupCode` | |
| PWD-3 (later) | A setup sends the code, a name, a login name, and a password. It adds the tenant `Recall`, an Admin who holds each Admin permission, and a Chosen credential in one save. A wrong code gets 403 and changes nothing. A setup after the first gets 409, and so does the loser of two setups at once. A successful setup voids the code. | `SetupInstall` | The database admits one first tenant |
| PWD-4 (later) | The first OIDC login of a new install still makes the Owner with no setup code, as [First login and the Owner](../authentication.md#first-login-and-the-owner) says, because the provider decides who reaches it. | `Provisioner` | The database admits one first tenant |
| PWD-5 (later) | Each password form post (login, setup, choose) needs a live `LoginAttempt` that the GET of its page wrote, and the binding cookie of that attempt. A post consumes the attempt, so a form posts once. A post with no attempt, a stale attempt, or another browser's attempt gets the `Refused` page. | `PasswordEndpoints`, `BindingCookie` | `ConsumeAttemptAsync` deletes the row |
| PWD-6 (later) | A login name that no credential holds runs one hash of a fixed value and gets the same 401 and message as a wrong password. A Stopped account runs one hash too. | `PasswordLogin` | |
| PWD-7 (later) | A right password of a Chosen credential gives `ProvisionOutcome.Provisioned` to `LoginCompletion`, so the web gets a session cookie and a native app gets a code, as an OIDC login does. A native password login shows no `Sign in to the Recall app?` page, because the person typed the password on the Server page. No route takes a password from an app, and `/auth/token` has no `password` grant. | `PasswordLogin`, `LoginCompletion` | |
| PWD-8 (later) | A chosen password has 15 to 256 code points after NFKC normalization. It may hold each Unicode character, also a space. No rule asks for a kind of character, and no password expires. The form body has a limit of 8 KiB. | `PasswordPolicy` | |
| PWD-9 (later) | A chosen password is refused when its whole value, with case ignored, is on the shipped list of common and leaked passwords, or equals the login name, a name of the person, or `recall`. The refusal names the reason. | `PasswordPolicy`, `CommonPasswords` | |
| PWD-10 (later) | The Server stores a password with `PasswordHasher<T>` from the shared framework, PBKDF2-HMAC-SHA512 at 220,000 iterations with a 128-bit salt. A login whose hash reports `SuccessRehashNeeded` stores a new hash in the same save. | `PasswordHashing` | |
| PWD-11 (later) | At most 2 hashes run at once. A request that waits more than 5 seconds for a slot gets 429 with `Retry-After`. | `PasswordHashing` | |
| PWD-12 (later) | The per-address limit of login starts also covers each password form post. After 5 wrong passwords in a row for one typed login name from one address (one IPv6 `/64`), each further try from that pair waits 30 seconds, doubling to 15 minutes. A wait gets 429 and runs no hash. A right password resets the pair. The pairs are in memory, so a restart clears them. A name that no credential holds counts the same, so the wait reveals nothing. | `LoginStartLimit`, `PasswordBackoff` | |
| PWD-13 (later) | `failed_count` counts wrong passwords in a row for the credential, from all addresses, and resets on a right one. At 100 the credential is Stopped. A Stopped credential refuses each password, also the right one, until an Admin issues a temporary password or the operator uses the owner claim. Its sessions keep working. | `PasswordCredential.NoteFailure` | |
| PWD-14 (later) | A caller with `manage_user_access` in a web or native session can issue a temporary password to a user of the tenant, also to a user with an OIDC link and to the caller. The caller must hold each permission of the role of that user. The Server makes the password from 4 random words of the EFF long list, joined by `-`, and only the answer holds it. The first issue also sets the login name. An issue ends each session and each PAT of that user, and records the audit action `TemporaryPasswordIssued` with no password. A PAT, an agent, and a provider token get 403. | `IssueTemporaryPassword`, `PermissionGuard.RequireSignedInPersonAsync` | |
| PWD-15 (later) | A temporary password is good for 7 days. It signs in to nothing but the choose page (PWD-16). After 7 days, the right temporary password gets the expired message and the wrong one gets the usual 401. | `PasswordLogin`, `PasswordCredential.IsExpired` | CHECK on `temporary_until <= changed_at + 7 days` |
| PWD-16 (later) | A right temporary password consumes the login attempt and writes a choose attempt with the user ID, good for 10 minutes. The choose post needs that attempt (PWD-5). Its password follows PWD-8 and PWD-9 and is not the temporary password. Then the credential is Chosen, the audit action `PasswordChosen` is recorded, and the session or the native code follows as in PWD-7. | `ChoosePassword`, `PasswordCredential.Choose` | |
| PWD-17 (later) | A person in a web or native session changes the password with the current password. A wrong current password counts toward PWD-12 and PWD-13. A change ends each other session and each PAT of the person and keeps the current session. It records the audit action `PasswordChanged`. | `ChangePassword`, `SessionStore.RevokeOthersAsync` | |
| PWD-18 (later) | A login name has 1 to 64 characters of letters, digits, `.`, `-`, `_`, and `@`. The Server stores it after NFKC normalization and a case fold, and a login form folds the typed name the same way. Two live users of the install cannot have one login name. | `LoginName.Of` | Unique index on `auth_password_credentials.login_name` |
| PWD-19 (later) | While a fresh `claim-owner` file exists, the login page offers `Set a new Owner password`. The post takes a login name and a password that follow PWD-8, PWD-9, and PWD-18. It gives the Admin that the claim picks a Chosen credential with these values and each Admin permission, ends each session and each PAT of that Admin, and removes the file, under the rules of [Recover a lost Owner](../authentication.md#recover-a-lost-owner). | `OwnerClaim`, `ClaimOwnerPassword` | |
| PWD-20 (later) | No password, temporary password, or hash reaches a log, a span, baggage, or an audit value. The setup code reaches only its one log line at boot. | Each record's `ToString`, `AuthQueryLogFilter`, `AuditModel` | Test with a marker value through each password route |
| PWD-21 (later) | A delete of a user, also through a delete of its tenant, removes its credential. | `RemoveLoginsWhenUserDeleted` | Foreign key on `user_id` |
| PWD-22 (later) | The last-Admin rule counts a password credential that is not Stopped as a way to sign in, the same as an OIDC link. So a delete, a demotion, or a permission removal gets 409 when it would remove the last Admin who holds each Admin permission and can sign in with a link or a password. | `LastAdmin.GuardAsync` | Exclusive tenant lock |
| PWD-23 (later) | A logout of a session that a password started ends it and goes to the signed-out page with no step at a provider. | `AuthEndpoints.LogoutAsync` | |

## 4. Permissions

None. An issue of a temporary password uses `manage_user_access`.

## 5. Endpoints and tools

The `/auth/password/*` routes are not in the OpenAPI document, as with the other `/auth/*` routes. [authentication.md](../authentication.md) gives their contract when they are built.

| Feature | HTTP | MCP tool | Permission |
|---|---|---|---|
| `PasswordLogin` | `POST /auth/password/login` | None | None |
| `SetupInstall` | `GET`, `POST /auth/password/setup` | None | The setup code |
| `ChoosePassword` | `POST /auth/password/choose` | None | A choose attempt |
| `ChangePassword` | `POST /auth/password/change` | None | A web or native session |
| `ClaimOwnerPassword` | `POST /auth/password/claim-owner` | None | A fresh `claim-owner` file |
| `IssueTemporaryPassword` | `POST /api/v1/users/{id}/temporary-password` | None | `manage_user_access`, in a web or native session |

## 6. Messages

| Rule | Status | Message |
|---|---|---|
| PWD-3 | 403 | The setup code is wrong. Find the current code in the Server log, then try again. |
| PWD-3 | 409 | This Server is already set up. Sign in instead. |
| PWD-6 | 401 | The login name or the password is wrong. |
| PWD-8 | 422 | Use at least 15 characters. A phrase of four words is long enough and easy to remember. |
| PWD-8 | 422 | Use no more than 256 characters. |
| PWD-9 | 422 | This password is on a list of common or leaked passwords. Pick another. |
| PWD-9 | 422 | A password cannot be your login name, your name, or Recall. Pick another. |
| PWD-11 | 429 | The Server is busy. Try again in a few seconds. |
| PWD-12 | 429 | Too many wrong tries. Wait {minutes} minutes, then try again. |
| PWD-13 | 401 | Password sign-in is stopped for this account after too many wrong tries. Ask an Admin for a temporary password. |
| PWD-15 | 401 | This temporary password expired. Ask an Admin for a new one. |
| PWD-16 | 422 | Pick a password that is not your temporary password. |
| PWD-17 | 403 | Your current password is wrong. |
| PWD-18 | 409 | This login name is in use. Pick another. |
| PWD-18 | 422 | A login name has up to 64 letters, digits, dots, dashes, underscores, or @ signs. |

## 7. Time zones

| Value | Stored as | Shown in the zone of |
|---|---|---|
| `changed_at`, `temporary_until` | UTC instant | The browser |

## 8. Open decisions

| Decision | Who decides | Rules that wait |
|---|---|---|
| The PWD-13 message tells a person who tries 100 times that the login name exists. The 100 tries take hours under PWD-12, so the spec accepts it. | The operator | PWD-13 |
| Behind a proxy, each client shares the address of the proxy, so one stranger's wrong tries make the real person wait under PWD-12 for that login name. The rule follows [Limit on login starts](../authentication.md#limit-on-login-starts) and reads no `X-Forwarded-For`. | The operator | PWD-12 |
| The 7 days of a temporary password and the 100 tries of PWD-13 are not settings. | The operator | PWD-13, PWD-15 |
