# Evidence that the ADRs cite

These files are copies of the output of throwaway prototypes. The prototypes ran on 2026-11-04 on a loaded Mac, and they are not in this repository. The files name the template and old commands because they are history, so the name check in [ADR 0017](../adr/0017-template-fixes-by-cherry-pick.md) skips this folder.

| Folder | Content | Cited by |
|---|---|---|
| `auth/` | Login proofs, the rejected forwarded token, and the local login commit | [ADR 0014](../adr/0014-app-owned-identity.md) |
| `transcribe/` | Each transcription run, as one JSON line | [ADR 0016](../adr/0016-audio-first-capture.md) |

The copies have these redactions, each marked in the file:

- Home paths in `auth/proof-a.txt`
- The OIDC `state` value in `auth/proof-e-stock-bff.txt`
- The session IDs (`sid`) in `auth/proof-b.txt` and `auth/proof-c.txt`
- The `pg_dump` restrict keys in `auth/schema-diff.txt`

The users and the email addresses are test data, and the `rcl_` values are the user IDs of those test users.
