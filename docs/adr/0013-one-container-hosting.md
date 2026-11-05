# Recall ships as one container with one volume

The audience is a person with a NAS and one compose file. So `docker compose up -d` starts one container and one volume. One container is the agent reading of the operator ruling "agree sqlite" of 2026-11-05. The operator did not say it in those words. `Recall.Server` serves the SPA, the API, and auth from one process. The database, the files, the Data Protection keys, the logs, and the models are all under `/data`. Backup is a copy of that volume.

## Evidence

The prototype is the local branch `recall-sqlite-proto`, SHA `e6073f5a5995fbf6a981dda2f41821ebbdb6d7f6`, which is not in this repository. Its `deploy/Dockerfile` and `deploy/entrypoint.sh` boot the app in one container. Appendix B of the program plan, which is also not in this repository, records an idle memory of 164 MiB, against 197 MiB with a Postgres sidecar. The 197 MiB is inferred, because no run had a sidecar. No raw output file for those numbers exists, so `docs/evidence/` has no copy of them.

The prototype ran two processes, the Server and the BFF, in the one container. It did not merge the hosts. The plan's R4 does that merge, so one process is a target and not a measured result. All numbers come from a loaded Mac with Docker on arm64, and no run was on a real NAS.

## Considered options

- **A Postgres sidecar.** See [ADR 0015](0015-sqlite-storage.md). We did not choose this option. It adds a second container and a database upgrade across major versions.
- **Keep the BFF as a second process.** See [ADR 0014](0014-app-owned-identity.md). We did not choose this option, because it exists to hold the tokens of a provider, and Recall no longer forwards those tokens.
- **Put the speech model in the image.** We did not choose this option. The model adds 661 MB to each pull. Recall downloads it on first use with a pinned hash.

## Consequences

- ADRs 0003, 0005, and 0006 describe the BFF. They stay true until R4 deletes it.
- [ADR 0004](0004-server-side-bff-sessions.md) becomes false at R1, because it puts `bffdb` and an advisory lock on Postgres. [ADR 0010](0010-domain-events-in-the-save-transaction.md) and [ADR 0012](0012-audit-log.md) also change at R1, because they name `xmin` and Postgres COPY.
- The web mic needs HTTPS, and many homelab installs use plain HTTP. R5 must give a TLS path that a person can finish. The native iOS app does not have this limit.
