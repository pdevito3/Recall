# Recall stores its data in one SQLite file

The operator picked SQLite in one container on 2026-11-05 ("agree sqlite"). One file gives a simple backup, a smaller image, and no database upgrade across major versions. The audience is a person with a NAS. The POC also ran this way. The plan's R1 builds it.

## Evidence

The prototype is `~/.claude/orchestrate/recall/prototypes/sqlite` on branch `recall-sqlite-proto`, SHA `e6073f5a5995fbf6a981dda2f41821ebbdb6d7f6`.

- `TestResults/*.tunit-report.json` gives 2312 of 2348 tests passed. Appendix A of `~/.claude/orchestrate/recall/docs/plan.md` records 311 more tests that the prototype excluded, because they depend on Npgsql. The 36 failures include access tests, such as `EndpointAccessSweepTests`, `UnauthorizedResponseTests`, and `TenantScopeFilterTests`. The first two stopped because the unnamed `/proto/*` endpoints broke the OpenAPI document, and the third stopped on a raw SQL syntax error. No access assertion failed, but none of those checks passed on SQLite, so R1 must prove them.
- `Recall.Server/Proto/ProtoHarness.cs` is the load harness. BackWave shared the file with atomic enqueue. Appendix A records writes at dictation pace with p95 389 ms and max 965 ms, and no `SQLITE_BUSY` in about 13,270 writes. No raw output file for these numbers stays in the prototype directory.

These numbers come from a loaded Mac with Docker on arm64. No run was on a real NAS.

## Considered options

- **A Postgres sidecar.** The author of the prototype recommended this option, and the operator did not choose it. Idle memory ties, at 164 MiB for SQLite and an inferred 197 MiB for Postgres with BackWave. The sidecar keeps the template data layer and its race tests, and it avoids a fork of 31 template app files and 12 test files. R3 and R4 fork the template in any case, so the fork that SQLite adds is smaller than it first looks.
- **Remove tenancy.** We did not choose this option. More than 40 Server files mention `TenantId`, and removal gives a person no new behavior. Recall keeps one seeded tenant, and it adds a per-user journal filter in addition to the tenant filter.

## Consequences

- The data layer forks from the template, and the program pays for that in R1 and in each later template fix. See [ADR 0017](0017-template-fixes-by-cherry-pick.md).
- [ADR 0008](0008-optimistic-concurrency-with-xmin.md) stays true until R1 replaces `xmin` with an explicit version column. R1 also replaces the advisory locks with a process-local lock.
- SQLite on a network mount can corrupt data or run a job twice, so R1 refuses a network file system at startup.
- A long write transaction stalls the job pumps. Appendix C of `~/.claude/orchestrate/recall/docs/plan.md` records a max of 4.8 s during a drain in the prototype, with no raw output file. Watch the max, and not only the p95.
