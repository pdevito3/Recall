# Recall stores its data in one SQLite file

> Changed by PR R1 of the plan: until R4 deletes the BFF, its sessions and its Data Protection keys are in a second file, `bff.db`, next to `recall.db`. A backup needs both files until then.

The operator ruled on 2026-11-05: "agree sqlite". One container is the agent reading of that ruling, as [ADR 0013](0013-one-container-hosting.md) says. One file gives a simple backup, a smaller image, and no database upgrade across major versions. The audience is a person with a NAS. The POC also ran this way. The plan's R1 builds it.

## Evidence

The prototype is the local branch `recall-sqlite-proto`, SHA `e6073f5a5995fbf6a981dda2f41821ebbdb6d7f6`, which is not in this repository. Neither is the program plan. `docs/evidence/` has no text file for this ADR, because the numbers come from 12 MB of test reports, prototype source, and the plan, and no raw output file exists for the load numbers.

- The TUnit reports of the prototype give 2312 of 2348 tests passed. Appendix A of the plan records 311 more tests that the prototype excluded, because they depend on Npgsql. Those 311 include the tenant and user isolation tests, such as `TenantQueryFilterTests`, `TenantOwnedRowsTests`, `NavigationTenantFilterTests`, and `UserReadAccessTests`. SQLite has not run them yet, and R1 must run them. The 36 failures include access tests, such as `EndpointAccessSweepTests`, `UnauthorizedResponseTests`, and `TenantScopeFilterTests`. The first two stopped because the unnamed `/proto/*` endpoints broke the OpenAPI document, and the third stopped on a raw SQL syntax error. No access assertion failed, but none of those checks passed on SQLite, so R1 must prove them.
- `Recall.Server/Proto/ProtoHarness.cs` is the load harness. BackWave shared the file with atomic enqueue. Appendix A records writes at dictation pace with p95 389 ms and max 965 ms, and no `SQLITE_BUSY` in about 13,270 writes. No raw output file for these numbers stays in the prototype directory.

These numbers come from a loaded Mac with Docker on arm64. No run was on a real NAS.

## Considered options

- **A Postgres sidecar.** The author of the prototype recommended this option, and the operator did not choose it. Idle memory is 164 MiB for SQLite and 197 MiB for Postgres with BackWave. The 197 MiB is inferred, because no run had a sidecar. The sidecar keeps the template data layer and its race tests, and it avoids the fork that [ADR 0017](0017-template-fixes-by-cherry-pick.md) counts. R3 and R4 fork the template in any case, so the fork that SQLite adds is smaller than it first looks.
- **Remove tenancy.** We did not choose this option. More than 40 Server files mention `TenantId`, and removal gives a person no new behavior. Recall keeps one seeded tenant, and it adds a per-user journal filter in addition to the tenant filter.

## Consequences

- The data layer forks from the template, and the program pays for that in R1 and in each later template fix. See [ADR 0017](0017-template-fixes-by-cherry-pick.md).
- R1 replaced the `xmin` of [ADR 0008](0008-optimistic-concurrency-with-xmin.md) with an explicit version column, and the advisory locks with a process-local lock.
- SQLite on a network mount can corrupt data or run a job twice, so the app refuses a network file system at startup.
- A long write transaction stalls the job pumps. Appendix C of the plan records a max of 4.8 s during a drain in the prototype, with no raw output file. Watch the max, and not only the p95.
