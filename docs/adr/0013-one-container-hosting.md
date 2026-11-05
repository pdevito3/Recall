# Recall ships as one container with one volume

The audience is a person with a NAS and one compose file. So `docker compose up -d` starts one container and one volume. `Recall.Server` serves the SPA, the API, and auth from one process. The database, the files, the Data Protection keys, the logs, and the models are all under `/data`. Backup is a copy of that volume.

## Evidence

The prototype is `~/.claude/orchestrate/recall/prototypes/sqlite` on branch `recall-sqlite-proto`, SHA `e6073f5a5995fbf6a981dda2f41821ebbdb6d7f6`. Its `deploy/Dockerfile` and `deploy/entrypoint.sh` boot the app in one container. Appendices A and B of `~/.claude/orchestrate/recall/docs/plan.md` record the results: idle memory is 164 MiB, against an inferred 197 MiB with a Postgres sidecar, and the image is 377 MB smaller. No raw output file for those two numbers stays in the prototype directory.

The prototype ran two processes, the Server and the BFF, in the one container. It did not merge the hosts. The plan's R4 does that merge, so one process is a target and not a measured result. All numbers come from a loaded Mac with Docker on arm64, and no run was on a real NAS.

## Considered options

- **A Postgres sidecar.** See [ADR 0015](0015-sqlite-storage.md). We did not choose this option. It adds a second container and a database upgrade across major versions.
- **Keep the BFF as a second process.** See [ADR 0014](0014-app-owned-identity.md). We did not choose this option, because it exists to hold the tokens of a provider, and Recall no longer forwards those tokens.
- **Put the speech model in the image.** We did not choose this option. The model adds 661 MB to each pull. Recall downloads it on first use with a pinned hash.

## Consequences

- ADRs 0003, 0004, 0005, and 0006 describe the BFF. They stay true until R4 deletes it.
- The web mic needs HTTPS, and many homelab installs use plain HTTP. R5 must give a TLS path that a person can finish. The native iOS app does not have this limit.
