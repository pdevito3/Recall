# Admins set login providers in the app

Phase 1 ([ADR 0024](0024-recall-signs-people-in-with-its-own-passwords.md)) gives each install a password login, and OIDC providers still come only from `Auth:Providers` at boot. A self-hoster who later wants their identity provider, for MFA or passkeys, has to edit the compose file, restart, and then has two users for one person, because nothing links an OIDC identity to a password user (issue #151). So an Admin adds a provider in the app, each person links it from their own session, and once that is safe, an Admin turns password login off. [The spec](../specs/login-providers.md) gives the rules.

## Decisions

- **Providers live in a table, and `Auth:Providers` stays as a read-only seed.** Existing installs keep their compose files. The two sources cannot share an ID or an issuer, and on a clash after a restart the environment wins, because an operator who edits the file means it. `LoginModes` merges both lists and rebuilds on a change, so no restart.
- **The client secret is protected with Data Protection, as the API key of a model provider is.** A lost key ring makes the secret unreadable. The row stays, its login button goes, and the settings page asks for the secret again. `KeyRingWatch` checks these secrets too.
- **The issuer of a provider is fixed.** A link is the pair of issuer and subject, so a changed issuer would silently orphan each link. To move, an Admin adds a provider and each person links again.
- **A person links only from their own session, never by email.** A matching email at a provider proves nothing about the person in Recall. The link starts with a CSRF post and binds the attempt to the session and its browser, so a stranger cannot attach their identity to someone else's account. The session must be at most 15 minutes old, so a stolen cookie cannot add a way in that outlives it.
- **Turning passwords off keeps a way in.** The switch is refused until each tenant has an Admin with each Admin permission and a link to a usable provider, and the last-Admin rule then stops counting passwords. The `claim-owner` file keeps working, because write access to `/data` already means control of the install, and it is the way back when every provider fails. A temporary password still adds a person, but it only links a provider, and the session starts after the link. Turning passwords On to add someone would let each old password back in.
- **`manage_user_access` gates the providers.** It can already issue a temporary password to anyone, which is as strong as choosing the provider, so a new permission would add a name and a migration and protect nothing more.

## Considered options

- **Move `Auth:Providers` into the table after one release.** It forces every install to migrate for no gain, and two sources need only a precedence rule.
- **A provider per tenant.** The login page runs before the Server knows a tenant, and installs have one tenant in practice.
- **Link by a verified email claim.** It is the most common shortcut and a common account takeover: whoever controls an email at any provider takes the account. ADR 0024 already rules it out.
- **Let an Admin link an identity for another person.** The Admin would type a subject they cannot verify. The person links it themselves, in one click.
- **Passwords fall back On when no provider is usable.** A lost key ring or a typo in the compose file would then let every old password back in. The disk file is the fallback instead.

## Consequences

- An Admin of any tenant changes the providers of the whole install.
- PWD-1, PWD-3, PWD-14, and PWD-22 of phase 1 change: each now names password login being On or a provider of either source.
- With passwords off, a new person needs a temporary password and an account at a provider, and signs in at that provider from then on.
- Sessions and personal access tokens that a password started keep working after passwords turn off, as they do after a stop in phase 1. An Admin removes a person by deleting the user.
- The last-Admin rule now ignores a link whose provider is gone. This also closes a gap in phase 1, where such a link still counted.
