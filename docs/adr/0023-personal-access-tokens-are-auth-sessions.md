# A personal access token is an auth session with a fixed grant

A person wants a script or an MCP client to call `/api/*` and `/mcp` as that person, with a value they paste once (issue #104). An agent needs a client at the identity provider and a tenant record, and a no-provider install has no provider. So the Server issues the credential itself.

So a personal access token (PAT) is a third kind of row in `auth_sessions`, next to the web and native sessions of [ADR 0014](0014-app-owned-identity.md):

1. The row has `client_kind = 'pat'` and an ID with the prefix `pat`. Like every session, the table is `[NotAudited]`, and `SessionStore` is its only writer. So the lookup, `revoke-others`, the removal of the logins of a deleted user, and the sweep reach a PAT with no new code.
2. The value is `rcl_pat_` and 32 random bytes. `AuthRegistration.SelectScheme` already sends each `Bearer rcl_*` to `AppSession`, with a provider and with none. The Server stores only the SHA-256 hash.
3. The grant is fixed at creation: a set of permissions and a journal access of None, Read, or ReadWrite. A different grant needs a new token, so no edit races a request and the grant needs no history. Write without read is not a value, because an update or a delete loads its row through the read filter.
4. `AppSessionHandler` emits `sub`, `client_id` = `sid` = the PAT ID, `client_kind = pat`, and `pat_grant`. `PatClaims` is the one codec of these claims. It reads a PAT only from an identity of the `AppSession` scheme, so a provider token cannot pose as one.
5. `CallerIdentity.PersonalAccessToken` and `Caller.PersonalAccessToken` are new kinds. The caller holds the permissions that both its user and its grant hold, and `PermissionGuard` reads the user rows on each check, so a lost permission applies on the next call. The caller is not a `Member`, so it gets no self exception of `RequireSelfOr` and does not pass `OwnerGuard`.
6. The journal access decides the journal owner. With None, `JournalOwnerId` is null, and the `JournalOwner` filter and `RequireJournalOwner` refuse with no new code. With Read, `JournalWriteRule` in `AppDbContext.PrepareWrite` refuses each save whose tracker adds, changes, or deletes a row whose root type declares the `JournalOwner` filter. A user delete removes the journal of that user in the database, outside the tracker, so a token with `manage_users` can still delete a user. A handler with an effect before its save, such as a file, a job, or a schedule, calls `RequireJournalWriter` first.
7. The audit client of a write is the PAT ID, because [ADR 0011](0011-agents-are-tenant-records.md) makes the client of the token the audit client. The audit user is the person.
8. No provider token may name the client `recall` or a client that starts with `pat_`, and no agent may have a `pat_` client ID. So `actor_client = pat_…` in an audit entry written after this decision means a PAT.

## Considered options

- **An audited tenant root for each token.** It gives an audit row for each create and revoke. But a mark of the last use writes the audited row once an hour for each active token and moves its version, `revoke-others` takes two transactions, and the credential leaves the auth layer that owns credentials. An audit action on the User gives the create and the revoke instead.
- **One `Caller.Person` for each caller that narrows a user.** It removes the duplicate intersection of the delegated agent. But it rewrites each match on `Member`, and a raw match on `Person` with the user ID would compile and let a PAT through a self check. A private helper that both kinds share removes the duplicate at a far smaller cost.
- **A child table for the grant.** It adds a read on each permission check, and a delete of a permission must clear it by hand. A fixed grant on the row needs neither.
- **A new `actor_token_id` audit column.** ADR 0011 already makes the client of the token the audit client.
- **The first-party client `recall` with a grant on the user.** The audit log could not tell a PAT from the app.

## Consequences

- The row of `auth_sessions` is wider, and three columns depend on its kind: the name, the grant, and the last use. CHECK constraints tie each one to the `pat` kind. In exchange, `revoke-others`, the user delete, the sweep, and the lookup work with no new code.
- "Never expires" is stored as the instant 9999-12-31 and reads as null. The lookup keeps `AccessExpiresAt > now` with no null branch, and web and native rows keep a required `ExpiresAt`. `DateTimeOffset.MaxValue` does not round-trip, so the sentinel is that midnight.
- The last use is up to an hour stale. The Server marks it off the request with a 2 s limit, so no request writes and none waits for the writer line. A mark in `OnCompleted` stalled the next request on a keep-alive connection by the full wait for the writer line.
- A grant cannot change. A person who wants another grant makes a new token.
- A PAT with no journal access also loses the user settings and the status gates of the model providers, because `UserSetting` is a journal row.
- A PAT cannot read its own user record without `read_users`. It can call `/auth/me`.
- Each switch over the caller kinds has a branch for the PAT: `Scope`, `JournalOwnerId`, `PermissionGuard`, `DenialFor`, `CallerTelemetry`, and `AuditActor`.
- A rule in `PrepareWrite` sees only the tracker. A repeat Finish and a Retry queue a job with an empty save, a slice upload writes its file before its save, and a Summary generate writes its schedule before its save. These four handlers call `RequireJournalWriter` first. A new handler with an effect before its save must call it too, and [multi-tenancy.md](../multi-tenancy.md#the-journal-of-a-person) states the rule.
- The guard on provider tokens read only `client_id` and `azp` before this decision, so a token with `applicationId=recall` resolved as a user of the app. The guard now reads each claim that `CurrentUserService` reads as the client, so that gap is closed too.
- An agent that was registered before this decision with a client ID that starts with `pat_` stops working. The guard refuses its provider tokens with 401, and the row stays. The operator registers the client again under another ID.
- A no-provider install cannot make a PAT until local login exists, because no one can sign in there. It can use a PAT made while a provider was set.
