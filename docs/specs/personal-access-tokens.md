# Spec: personal access tokens

## 1. Scope

A person makes a personal access token (PAT) in the SPA and pastes it once into a script or an MCP client. The client then calls `/api/*` and `/mcp` as that person, inside a grant the person picked. Issue #104 gives the reasons. [ADR 0023](../adr/0023-personal-access-tokens-are-auth-sessions.md) records the design.

This spec leaves out:

- gRPC, which R4 removed;
- the journal MCP tools, which are a separate issue;
- a change to the grant after creation;
- a PAT that makes a PAT.

## 2. Entities and states

| Entity | Root of | Fields that a user sets | States |
|---|---|---|---|
| A PAT, a row of `auth_sessions` with the kind `pat` | None | name, permissions, journal access, expiry | Live, Revoked |

Expired is not a stored state. It comes from the expiry and the current time.

| From | To | Method | Who |
|---|---|---|---|
| Live | Revoked | `SessionStore.RevokePersonalAccessTokenAsync` | Its person, or a caller with `manage_user_access` in the tenant, both in a web or native session |
| Live | Revoked | `SessionStore.RevokeOthersAsync` | Its person, in a web or native session |

## 3. Rules

| ID | Rule | Enforced by | Backstop |
|---|---|---|---|
| PAT-1 | A PAT value is `rcl_pat_` and 32 random bytes. The Server stores only its SHA-256 hash, and only the create response holds the value. A list or a read never gives it. | `SessionStore.StartPersonalAccessTokenAsync` | Unique index on `auth_sessions.access_hash` |
| PAT-2 (later) | Only a person in a web or native session creates, lists, or revokes PATs. A PAT, an agent, or any other client gets 403. | `Caller.RequireMember` | |
| PAT-3 (later) | A grant holds only permissions that its creator holds at creation. A grant with any other permission gives 403 and makes no PAT. | `AddPersonalAccessToken` | |
| PAT-4 | A PAT holds the permissions that both its user and its grant hold, read on each check. A permission that the user loses applies on the next call. | `Caller.PersonalAccessToken`, `PermissionGuard` | |
| PAT-5 | The journal access of a grant is None, Read, or ReadWrite. A request for write without read gives 422, because an update loads its row through the read filter. | `JournalAccess.Of` | |
| PAT-6 | A PAT with no journal access sees no journal row. A route with an ID gives 404, and a route with no ID gives 403. It also loses the user settings, because they are journal rows. | `CallerIdentity.JournalOwnerId`, the `JournalOwner` filter | `JournalRulesTests` |
| PAT-7 | A PAT with no journal write saves no tracked add, change, or delete of a journal row, and leaves no effect of a refused change, such as a file, a job, or a schedule. A user delete by a PAT with `manage_users` still removes the journal of that user, because the database removes it and the save does not track it. | `JournalWriteRule` in `AppDbContext.PrepareWrite`, `RequireJournalWriter` in `StoreRecordingSlice`, `FinishRecording`, `ChangeRecordings`, and `GenerateSummary` | |
| PAT-8 (later) | A create sends exactly one of `expiresAt`, which is in the future, and `neverExpires: true`. Any other body gives 422. The SPA offers 90 days from the browser day as the default. | `AddPersonalAccessToken`, the SPA form schema | |
| PAT-9 | A revoked or expired PAT gets 401 on its next request. | `SessionStore.FindPersonalAccessTokenAsync` | |
| PAT-10 | `revoke-others` from a session revokes each PAT of the user, and a PAT that calls it gets 403. A delete of the user or of its tenant removes each PAT of the user. | `SessionStore.RevokeOthersAsync`, `InteractiveSessionOnly`, `RemoveLoginsWhenUserDeleted` | |
| PAT-11 | A PAT works only as a bearer, and needs no `X-CSRF` header. A PAT value in the cookie authenticates nothing. | `AppSessionHandler` | |
| PAT-12 | A PAT does not pass the self exception of `RequireSelfOr` or the Owner gate. | `Caller.RequireSelfOr`, `OwnerGuard` | |
| PAT-13 (later) | An Admin lists the PATs of another user of the tenant with `read_users`, and revokes one with `manage_user_access`. A user of another tenant gives 404. A person with neither permission sees only their own PATs. No list holds a value. | `GetUserPersonalAccessTokenList`, `RevokePersonalAccessToken` | Tenant filter on `users` |
| PAT-14 | Each write by a PAT records the PAT ID as `actor_client` and the person as `actor_user_id`. | `AuditActor.Of` | |
| PAT-19 | A create and a revoke of a PAT record an audit action with the PAT ID, its name, its grant, and its expiry, so a reader can map a `pat_…` actor to a name after the row is gone. | `AccessTokenCreated`, `AccessTokenRevoked` | |
| PAT-15 | Only an app session is the client `recall`, and only a PAT is a client `pat_…`. A provider token whose client, read from `client_id`, `azp`, or `applicationId`, is `recall` or starts with `pat_` gets 401. An agent with a `pat_` client ID gives 422. | `NamesAnAppSession`, `Agent.Create` | |
| PAT-16 | The Server marks the last use at most once an hour per PAT, off the request. No request waits for the mark. | `AppSessionHandler`, `SessionStore.NotePersonalAccessTokenUseAsync` | |
| PAT-17 (later) | No PAT value and no hash of it reaches a log, a span, baggage, or an audit value. | Each record's `ToString`, `AuditModel` | Test with a marker value |
| PAT-18 (later) | A PAT reaches `/api/*` and `/mcp` on an install with a provider and on one with none. `tools/list` shows only the tools that the PAT's permissions allow. | `AuthRegistration.SelectScheme`, `McpToolAccess` | |

## 4. Permissions

None. A PAT uses the existing permissions: `read_users` to list the PATs of another user, and `manage_user_access` to revoke them.

## 5. Endpoints and tools

| Feature | HTTP | MCP tool | Permission |
|---|---|---|---|
| `AddPersonalAccessToken` | `POST /api/v1/personal-access-tokens` | None | A session |
| `GetPersonalAccessTokenList` | `GET /api/v1/personal-access-tokens` | None | A session |
| `GetUserPersonalAccessTokenList` | `GET /api/v1/users/{id}/personal-access-tokens` | None | `read_users`, or self |
| `RevokePersonalAccessToken` | `POST /api/v1/personal-access-tokens/{id}/revoke` | None | `manage_user_access`, or self |

## 6. Messages

| Rule | Status | Message |
|---|---|---|
| PAT-2 | 403 | A personal access token cannot do this. Sign in to Recall to do it. |
| PAT-4 | 403 | This personal access token has no grant to {words}. Make a token that has it. |
| PAT-5 | 422 | A token that changes your journal must also read it. |
| PAT-7 | 403 | This personal access token can read your journal but cannot change it. |
| PAT-8 | 422 | Pick an expiry date in the future, or pick Never. |
| PAT-15 | 422 | A client ID cannot start with pat_, because a personal access token has that ID. |

## 7. Time zones

| Value | Stored as | Shown in the zone of |
|---|---|---|
| Created, expires, last used, revoked | UTC instant. "Never" is the instant 9999-12-31 and reads as null. | The browser |
| The picked expiry date | The end of that day in the browser zone, as a UTC instant | The browser |

## 8. Open decisions

| Question | Who decides | Rules that wait |
|---|---|---|
| A no-provider install cannot sign in, so it cannot make a PAT until local login exists. Does use of a PAT made earlier meet the acceptance box? | Operator | PAT-18 |
| May a grant hold `manage_model_providers`, which agents may not hold? | Operator | PAT-3 |
