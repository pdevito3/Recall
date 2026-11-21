# Recall signs people in with its own passwords

A self-hoster must sign in within minutes of the first `docker compose up`, and many homelab installs have no identity provider and no SMTP (issue #84). So Recall has its own password login, on by default, and OIDC stays an option. A password is one more login mode of [ADR 0014](0014-app-owned-identity.md): a right password gives the same `Provisioned` outcome as an OIDC callback, and `LoginCompletion` issues the same web or native session. The prototype commit `988cb19a` showed this shape adds one file and one table ([local-commit-stat.txt](../evidence/auth/local-commit-stat.txt)). [The spec](../specs/password-login.md) gives the rules.

## Decisions

- **A native app never sees a password.** The app opens `/auth/login` in the system browser, the person types the password on the Server page, and the app redeems a code with PKCE, as with OIDC. OAuth 2.1 removes the password grant, and RFC 8252 asks for the system browser. So `/auth/token` gets no `password` grant, and the app changes almost nothing. The `Sign in to the Recall app?` tap goes for a password login, because RFC 8252 section 8.6 guards against a provider that signs a person in with no click, and a typed password is the click.
- **The first setup needs a code from the log.** With OIDC, the provider decides who reaches the first login. With a password, nothing does, so a stranger who reaches a fresh install on a public port could take it. The Server writes a one-use code to the console and to `/data/logs` at boot while no tenant exists. The cost is one `docker compose logs`. The code is the one secret that a log holds, and setup voids it.
- **An Admin adds a person with a temporary password, and that person picks their own.** Email needs SMTP, which many NAS installs lack. An invite link needs no SMTP either, but a temporary password also works when the Admin hands over a phone, and it needs no table of links. The Server makes it from 4 random words, so an Admin cannot pick a password that the person uses elsewhere. It signs in to nothing but the page that picks a new password, and it lasts 7 days. A reset is the same act.
- **PBKDF2-HMAC-SHA512 at 220,000 iterations, through `PasswordHasher<T>`.** OWASP puts Argon2id first, but .NET has no built-in Argon2id, so it would add a native package, and it needs 19 to 46 MiB for each login that runs at once against the 200 MiB idle budget of #47. `PasswordHasher<T>` is in the shared framework, its format names its own cost, and it reports when a hash needs a new cost. At most 2 hashes run at once.
- **The NIST SP 800-63B-4 password rules.** A password used alone needs 15 characters. No rule asks for a kind of character, no password expires, and a list of common and leaked passwords ships in the image, because a homelab install may have no internet.
- **A wait, not a lockout.** A hard lockout after a few wrong tries lets a stranger lock the Owner out. So the wait applies to one pair of a typed login name and an address, and it doubles to 15 minutes. NIST caps wrong tries in a row at 100, so the 100th stops password login for the account until an Admin issues a temporary password or the operator uses the owner claim. Sessions keep working through both.
- **The `claim-owner` file recovers a lost Owner password.** ADR 0014 already chose the disk as the proof of control over an install, so the same file lets the operator set a new Owner password.

## Considered options

- **OIDC only, with a Pocket ID example.** The self-hoster runs and configures a second service before the first sign-in, which is the friction #84 removes.
- **Passkeys only.** A passkey needs a TLS origin, and many first installs use `http://LAN-IP`. Passkeys and MFA stay with the provider.
- **A password grant at `/auth/token` for the app.** It is the shortest app code, but OAuth 2.1 and RFC 9700 forbid it, and the app would hold the password.
- **Argon2id through a native package.** See the hash decision above.

## Consequences

- Each install has a login method, so `no_login_method` goes. An install that sets a provider shows both the password form and the provider button, until phase 2 adds a switch that turns password login off.
- The 100th wrong try in a row tells the person who made it that the login name exists. Under the wait, that takes hours.
- Behind a proxy, all clients share one address, so a stranger's wrong tries make the real person wait for that login name. Recall reads no `X-Forwarded-For`, as with the limit of login starts.
- An OIDC user and a password user are two users until phase 2 adds a link from a session. Recall never links two logins by a matching email.
