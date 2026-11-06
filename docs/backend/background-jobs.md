# Background jobs

Work that does not fit in a request runs as a BackWave job. A job survives a crash and a restart, because its row is in the database of the app. `Recall.Server/Resources/Jobs/` holds the host setup and the one health job. `Recall.Server/Resources/Entrypoints/Jobs/` holds the dashboard.

## Rules for a job

1. Write a job as a `[Job("wire-name", Queue = JobHosting.SystemQueue)]` method. The wire name stays the same for the life of the job, because a row in the database names it.
2. A payload holds IDs only. BackWave stores a payload as plain text, and the Owner can read a job row. The job reads the text from the database. `JobPayloadTests` refuses a job parameter that is a `string` with a name that does not end in `Id`, and each type that is not an ID.
3. A job has no caller. Open a scope with `IServiceScopeFactory.CreateSystemScope()`, because a plain scope sees no tenant, so the query filters hide each row.
4. A job can run more than one time. A crash after the work and before the commit of the job row runs the work again. Make the work idempotent.
5. A job honors its `CancellationToken`. At shutdown a job has `JobHosting.JobShutdownBudget` less the fifth that BackWave keeps to give back the leases, so about 4.8 s. If it does not stop, its lease ends and a later start runs it again.
6. An exception message holds no user text. BackWave copies the message to the span of the job, to the log, and to the terminal cause in the job row, which the dashboard shows.
7. Set `[Retry(maxAttempts, backoffSeconds...)]` when the default of 10 attempts with a backoff to 5 minutes does not fit the job.

## Enqueue a job

To write rows and enqueue a job as one unit, use `AppDbContext.SaveAndEnqueueAsync`. It runs the handlers of the domain events first, then it opens one transaction for the changes, the audit entries, and the enqueue. Give that transaction to `BackWaveClient.EnqueueAsync`:

<!-- code: tests/Recall.IntegrationTests/Jobs/SaveAndEnqueueTests.cs -->
```csharp
await db.SaveAndEnqueueAsync(async (transaction, ct) =>
    jobId = await client.EnqueueAsync(new SystemHealth(), dueTime: DateTimeOffset.UtcNow.AddDays(1), transaction: transaction, cancellationToken: ct));
```

If the save, the enqueue, or the commit fails, neither the rows nor the job stay. The context then refuses each later save, as after each failed save. `SaveChangesAsync` still refuses a transaction that you opened.

A worker sees a job that this path committed at its next poll, not at once, because the commit of a caller wakes no worker. The first use of the store migrates the job tables and needs the write lock, so the host does this with its first enqueue at start. Do not use the store for the first time inside a transaction.

## The host

| Setting | Value | Why |
|---|---|---|
| Store | The SQLite file of the app, tables `backwave_*`, `AutoMigrate` on, busy timeout 30 s | One file holds the rows and the jobs, so one transaction holds both. The busy timeout is the one that the app connections use. |
| Worker group | One group named `system`, on the queue `system` | R9 and R11 add queues to this group. |
| `Jobs__PollSeconds` | 5 | A transactional enqueue waits for one poll. An idle poll writes nothing: at 1 s, 5 s, and 30 s, an idle server wrote no byte to the database, the WAL, or the `-shm` file in 15 minutes. |
| `MaintenanceInterval` | 5 s, the BackWave default | The sweep that ends a lapsed lease writes nothing when no lease has lapsed. |
| `LeaseDuration` | 60 s, the BackWave default | The job of a killed process runs again after its lease ends, the next sweep, its retry backoff, and the next poll. For the health job that took about 70 s. After a clean stop it runs at the next start. |
| `HostOptions.ServicesStopConcurrently` | true | The web host waits for each open request. A live page of the dashboard is open until the host timeout. With a stop in series, the workers would stop after that and give back no job. |
| `HostOptions.ShutdownTimeout` | 8 s | `docker stop` kills the process after 10 s. The telemetry files flush after the host stops. |
| `ShutdownBudget` | 6 s | BackWave clamps it to 4/5 of the host timeout. |
| `KeepSucceeded` | 14 days | The dashboard shows a finished job for as long as a dead-letter job. |

The host enqueues one `system-health` job at each start. A recurring schedule would write to the disk at each run and wake the disk of a NAS. `Jobs__HealthJob__HoldSeconds` keeps the job in its run, and `Jobs__HealthJob__FailOnPurpose` makes it fail, so a check of the host can kill a process during a job or fill the dead-letter list.

BackWave starts a span for each poll. `EmptyClaimSpanFilter` drops the span of a poll that claimed no job, so an idle server writes no span to `telemetry/`. The BackWave metrics add about 2 KB to each metric export, which runs each 60 s with or without BackWave.

## The dashboard

The dashboard is at `/backwave`. `JobDashboard` lets only the Owner see it or act in it, and gives each other caller a 403 with an empty body. It reads the Owner through `IOwnerGuard`, the rule that the support bundle uses.

A browser that opens the page sends no `X-CSRF` header, so `AppSessionHandler` accepts a session cookie on this path with no header. The session cookie is `SameSite=Strict`, and BackWave checks an antiforgery token on each write.

## The license

BackWave is under the PolyForm Shield License 1.0.0, which needs each copy to carry the terms and the Required Notice. `Recall.Server/ThirdPartyNotices/BackWave/LICENSE.md` is the file of the package, byte for byte. `dotnet publish` copies it to `ThirdPartyNotices/BackWave/LICENSE.md` in its output, so the image has it at `/app/ThirdPartyNotices/BackWave/LICENSE.md`. Replace the file when the version of BackWave changes.
