# Background jobs

Work that does not fit in a request runs as a BackWave job. A job survives a crash and a restart, because its row is in the database of the app. `Recall.Server/Resources/Jobs/` holds the host setup and the one health job. `Recall.Server/Resources/Entrypoints/Jobs/` holds the dashboard.

## Rules for a job

1. Write a job as a `[Job("wire-name", Queue = JobHosting.SystemQueue)]` method. The wire name stays the same for the life of the job, because a row in the database names it.
2. A payload holds IDs only. BackWave stores a payload as plain text, and the Owner can read a job row. The job reads the text from the database. `JobPayloadTests` refuses a job parameter that is a `string` with a name other than `id` or a name that does not end in `Id`, and each type that is not an ID.
3. A job has no caller. Open a scope with `IServiceScopeFactory.CreateSystemScope()`, because a plain scope sees no tenant, so the query filters hide each row.
4. A job can run more than one time. A crash after the work and before the commit of the job row runs the work again. Make the work idempotent.
5. A job honors its `CancellationToken`. The token ends at the moment the host begins to stop, so a job cannot use the stop to finish its work. The next 4.8 s are for the job to unwind and for BackWave to give back the leases. A job that has not stopped after that keeps its lease until the lease ends, and a later start runs it again.
6. Do not rely on an exception message. `JobFailureFilter` wraps each job, so BackWave records the type and the stack frames of a thrown exception, and no message and no inner exception. The job row, the job history, the span, the support bundle, and the dashboard get only this. `Telemetry.MarkFailed` obeys the same rule for each child span inside a job, such as a MediatR request or a SQL command. To show a text to the Owner, throw `JobSafeTextException` with a text that holds no user text. The filter does not cover two things. The span of the HTTP client holds the URL and the exception of the call, with the text of .NET, so a job puts no user text in a URL. A log line is yours, so job code logs the type of an exception and not its message.
7. Each claim counts an attempt, also when the host stops during the job. A lease given back on the last attempt dead-letters the job. Thus three restarts during a long job with `[Retry(3...)]` send it to the dead letters, with no fault in the job. The Owner requeues it at `/backwave/failures`.
8. Keep the default of 10 attempts, with a backoff to 5 minutes, for a job that can run longer than a restart interval. R9 and R11 use this default for a transcription job or a long model call, and they save their progress, so that each attempt continues the work. Set `[Retry(maxAttempts, backoffSeconds...)]` only for a short job where the default does not fit.

## Enqueue a job

To write rows and enqueue a job as one unit, use `AppDbContext.SaveAndEnqueueAsync`. It runs the handlers of the domain events first, then it opens one transaction for the changes, the audit entries, and the enqueue. The callback gets a `JobEnqueuer` that is bound to that transaction:

<!-- code: tests/Recall.IntegrationTests/Jobs/SaveAndEnqueueTests.cs -->
```csharp
await db.SaveAndEnqueueAsync(async (jobs, ct) =>
    jobId = await jobs.EnqueueAsync(new SystemHealth(), DateTimeOffset.UtcNow.AddDays(1), ct));
```

If the save, the enqueue, or the commit fails, neither the rows nor the job stay. The context then refuses each later save, as after each failed save. `SaveChangesAsync` still refuses a transaction that you opened.

A worker sees a job that this path committed at its next poll, not at once, because the commit of a caller wakes no worker.

Inside the callback, do no BackWave work other than the enqueuer. A BackWave call with its own connection waits for the write lock that this save holds, so each other save waits for up to 30 s. Only the code in `Resources/Jobs/` takes a `BackWaveClient`. `JobClientUseTests` fails on a field or a parameter of that type in other code, and it cannot see a call of `GetRequiredService<BackWaveClient>()`. The first use of the store migrates the job tables on its own connection, so `SaveAndEnqueueAsync` makes the store ready before it opens its transaction.

If the job store has a fault, `SaveAndEnqueueAsync` throws before it saves, and the rows do not stay. A feature that must keep the text of a person when the jobs are off saves the text first with `SaveChangesAsync`, then enqueues in a second save. R9 and R11 make this choice.

## The host

| Setting | Value | Why |
|---|---|---|
| Store | The SQLite file of the app, tables `backwave_*`, `AutoMigrate` on, busy timeout 30 s | One file holds the rows and the jobs, so one transaction holds both. The busy timeout is the one that the app connections use. |
| Worker group | One group named `system`, on the queue `system` | R9 and R11 add queues to this group. |
| `Jobs__PollSeconds` | 5 | A transactional enqueue waits for one poll. An idle poll writes nothing: at 1 s, 5 s, and 30 s, an idle server wrote no byte to the database, the WAL, or the `-shm` file in 15 minutes. |
| `MaintenanceInterval` | 5 s, the BackWave default | The sweep that ends a lapsed lease writes nothing when no lease has lapsed. |
| `LeaseDuration` | 60 s, the BackWave default | The job of a killed process runs again after its lease ends, the next sweep, its retry backoff, and the next poll. For the health job that took about 70 s. After a clean stop it runs at the next start. |
| `HostOptions.ServicesStopConcurrently` | true | The web host waits for each open request. With a stop in series, the workers would stop after that and could give back no job. |
| `HostOptions.ShutdownTimeout` | 8 s | `docker stop` kills the process after 10 s. The telemetry files flush after the host stops. |
| `ShutdownBudget` | 6 s | BackWave clamps it to 4/5 of the host timeout. The token of a job ends when the stop begins. The budget does not give a job more time to work. It gives a job about 4.8 s to unwind, and BackWave the rest to give back the leases. |
| `KeepSucceeded` | 14 days | The dashboard shows a finished job for as long as a dead-letter job. |

`/health` includes `JobHostHealthCheck`, which reads memory only. A store fault that is not transient, such as a full disk, a lost table, or a schema that the adapter refuses, stops the jobs until the next start. BackWave has no call that restarts a worker group: `WorkerGroupService` is internal and a halt has no clear path. The journal stays up. `/health` answers 503 with a body that says the jobs stopped and names the worker group and the type of the fault, and the server logs one line at the level `Fatal` that says to restart. See [The background jobs stopped](../troubleshooting.md#the-background-jobs-stopped). A lock or a fault at the start does not stop the boot, because `JobHostWatch` uses the store behind the start of the host. A skew in the version number is not a fault: BackWave migrates a lower version or a text, and it logs a warning for a higher version. A skew that the migration cannot fix is a fault, and the test `a_fault_in_the_job_store_at_the_boot...` gives one.

The host enqueues one `system-health` job at each start. A recurring schedule would write to the disk at each run and wake the disk of a NAS. `Jobs__HealthJob__HoldSeconds` keeps the job in its run, and `Jobs__HealthJob__FailOnPurpose` makes it fail, so a check of the host can kill a process during a job or fill the dead-letter list. These two settings work only in `Development` and in the test environments. `Jobs__RunWorkers=false` gives a test host no worker. It also works only in these environments.

BackWave starts a span for each poll. `EmptyClaimSpanFilter` drops the span of a poll that claimed no job, so an idle server writes no span to `telemetry/`. The BackWave metrics add about 2 KB to each metric export, which runs each 60 s with or without BackWave.

## The dashboard

The dashboard is at `/backwave`. `JobDashboard` lets only the Owner see it or act in it. It reads the Owner through `IOwnerGuard`, the rule that the support bundle uses. A page that is refused is a 403 with a short page that has a link to sign in, so a person whose cookie ended can get back. The session cookie is SameSite=Strict, so the redirect after a click at a provider on another site comes with no cookie. That refusal reloads once after 1 s, and the reload sends the cookie. A refused write action or live request is a 403 with an empty body.

BackWave checks the permission only at the start of a request, and a live page holds one request open. `JobDashboard.LiveStreamLimit` ends this request after 30 s, and at once when the host stops. The browser then opens a new request, and this checks the Owner again. So a person who lost the role or signed out sees job data for at most 30 s more. A stop of the host does not wait for a live page.

`AddBackWaveDashboardMetrics()` adds the throughput panel of the overview. It keeps a bounded buffer in memory and writes nothing to the disk.

A browser that opens the page sends no `X-CSRF` header, so `AppSessionHandler` accepts a session cookie on this path with no header. The session cookie is `SameSite=Strict`, and BackWave checks an antiforgery token on each write.

## The license

BackWave is under the PolyForm Shield License 1.0.0, which needs each copy to carry the terms and the Required Notice. `Recall.Server/ThirdPartyNotices/BackWave/LICENSE.md` is the file of the package, byte for byte. `dotnet publish` copies it to `ThirdPartyNotices/BackWave/LICENSE.md` in its output, so the image has it at `/app/ThirdPartyNotices/BackWave/LICENSE.md`. Replace the file when the version of BackWave changes.
