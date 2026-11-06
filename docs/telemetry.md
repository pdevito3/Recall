# Telemetry

The server exports traces, metrics, and logs with OpenTelemetry. In development, the Aspire dashboard shows them at https://localhost:24700 (the `aspire run` output gives the login link). An isolated run (`--isolated`) uses a random port. `dotnet aspire ps` gives its URL. The server also writes all three signals to files beside the database. See [Files](#files) and [troubleshooting.md](troubleshooting.md).

## Privacy rule

A span, a log, a metric tag, and a telemetry file carry names, identifiers, and counts. They never carry user-entered content or personal data, such as names or email addresses. Journal text is the first example: Raw, Cleaned, a Revision, a Synopsis, a Summary, and the audio of a Session never reach a log, a span, or a support bundle. Every helper below takes its tags from the caller, so each call site must obey this rule.

The rule also covers an exception. A client error can hold text from the caller in its message, so it never becomes a span event or a log with that message. Do not write a route or a query key that holds user text, because the request log and the span keep the path. They also keep the host and the scheme, because a reverse proxy fault shows there. Neither keeps the `User-Agent` header. A caller can put any text in the path, the host, the scheme, or a query key, and the server keeps that text. Journal text does not go there, because no client of Recall sends it there. `SupportBundleTests` sends a marker through the HTTP entrypoints and through each text argument of each MCP tool that the Owner sees, and searches the whole bundle and the captured logs for it. The test finds the tools with `tools/list`, so a new MCP tool that the Owner sees is in the test at once. The test does not reach `add_tenant`, because the Owner does not see it. An argument with a name that ends in `id` gets a real tenant id and no marker, so do not give a text argument such a name. When you add an HTTP entrypoint that takes user text, add it to that test.

`appsettings.json` keeps `Microsoft.AspNetCore.Hosting.Diagnostics` at `Warning`. At `Information` or lower, this category writes each query string, with its values, to each log sink. A list filter can hold text from the caller. No journal route takes text in a query, so a Session, a Revision, a Person, and a Correction do not reach a log in this way.

`SupportBundleTests.JournalText.cs` plants a marker in Raw, Cleaned, a Person, a Correction, and the metadata of a Session. It uses real writes, edits, a save that loses, saves that the server refuses, and saves that fail in the database. Then it searches the bundle, each file, each OTLP export that a test collector takes, and the captured logs. Its host logs at `Debug` in each category except the one above, so the search sees more than a shipped server writes. The search does not see `Verbose`, the categories of Kestrel, because the host has no socket, or a line that a Bundle host writes outside a request. A sentinel must reach the log files, the trace files, the OTLP logs, and the OTLP traces before the search starts, so a search does not pass on a sink that has not had the line yet. The sentinel proves the order and not each delivery: a line that a full queue lost is not in the search of that sink.

The same file proves the rule for an exception. It sends words that make a JSON reader throw, and bodies that the endpoint cannot read. It also makes the database refuse each journal save, so the exception reaches `@x` and a span. The text of that fault must reach each sink, and the marker must not. Thus the channel is active, and the journal text is not in it. Two guards do this work, and the test fails without each of them. `ProseMirrorNode.TryRead` catches the throw of a reader, because the message of a duplicate key names the key. `DomainProblemDetails.IsClientError` keeps a `BadHttpRequestException` out of the log, because the path of a `JsonException` names a key that the type does not have. The test proves what these paths do today, and the rule still applies to each new `throw`: do not put journal text in the message of an exception. A server fault keeps its message, up to the cap, in `@x` and in the exception event of its span.

An MCP call also keeps some fields of the protocol, and a caller can put any text in these fields. No client of Recall sends journal text there, so they stay:

| Field | Where the server keeps it |
|---|---|
| The `name` and `version` of `clientInfo` | `EndpointName` in the logs of the MCP SDK |
| A method that does not exist | `Method` in the logs of the MCP SDK |
| A tool name that does not exist | `gen_ai.tool.name` on the span of the HTTP request |
| The `id` argument of `get_tenant` and `update_tenant` | `app.tenant_id` on the span of `GetTenant.Query` and `UpdateTenant.Command` |

`SqlSpanInterceptor` makes a span for each SQL command of the Server, and it copies the SQL text into `db.query.text`. Thus a value from the caller must go to the database as a parameter, never as a literal in the SQL text. `CustomQueryKitConfiguration` sets `ParameterizeFilterValues`, so the SQL text of a list query has a parameter name in place of each filter value. The value of a QueryKit `CustomOperation` still goes into the SQL text as a literal.

The request spans of the server keep each query key in `url.query` and replace each value with `Redacted`, for example `?filters=Redacted&pageSize=Redacted`. The ASP.NET Core instrumentation does this by default, but Aspire turns it off in development. Thus `HostExtensions.cs` sets `url.query` again.

## What a trace looks like

A request gives one trace with this shape:

```
Server   GET api/v{version}/users
└─ GetUserList.Query                (RequestTracingBehavior)
   ├─ SQL: users.list.count         (SQL span, the count of the paged list)
   └─ SQL: users.list               (SQL span, tagged query)
```

A command whose save runs a domain event handler has one more level. The handler span is a child of the feature slice span:

```
Server   PUT api/v{version}/tenants/{id}
└─ UpdateTenant.Command                (RequestTracingBehavior)
   ├─ SQL: tenants.update.load
   ├─ domain_event.handle              (one span for each handler)
   │  └─ SQL: <the queries of the handler>
   └─ SQL: <the save>
```

## Where the code is

| File | Purpose |
|------|---------|
| `Recall.Server/Resources/Telemetry.cs` | The `ActivitySource`, the `Meter`, the shared metrics, `Trace`, `AddEvent`, `TraceRoot`, and the `TraceContext` helpers |
| `Recall.Server/Resources/RequestTracingBehavior.cs` | MediatR behavior. Gives each feature slice its own span and its duration metric |
| `Recall.Server/Resources/LoggerExtensions.cs` | `Here()`, which adds the call site to a log event |
| `Recall.Server/Databases/QueryTagging.cs` | `TagWithOperationCallSite`, which names an EF query |
| `Recall.Server/Databases/QueryTagEnrichment.cs` | Copies the query tags into the SQL span |
| `Recall.Server/Databases/DomainEventDispatcher.cs` | Runs each domain event handler in its `domain_event.handle` span |
| `Recall.Server/Resources/Extensions/HostExtensions.cs` | OpenTelemetry and Serilog registration, resource attributes, framework meters, request log level |
| `Recall.Server/Resources/FileTelemetry/` | The rotating files of the logs, the traces, and the metrics, and the two exporters |
| `Recall.Server/Domain/Support/` | The support bundle |

## Log, span, or event

For the rules on what each backend feature must emit, see [backend/feature-telemetry.md](backend/feature-telemetry.md).

Each signal answers a different question. Pick the signal from the question:

| Question | Signal | Helper |
|---|---|---|
| How long did this unit of work take, and what did it call? | Span | `Telemetry.Trace` |
| When did a point inside a span occur? | Span event | `Telemetry.AddEvent` |
| What did the app decide, and why? | Log | `logger.Here().Information(...)` |
| How many, and how fast, over time? | Metric | `Telemetry.Meter` |

The feature slice span, the HTTP span, and the SQL span already exist for each request. Add a child span only for work that has its own duration, such as a call to an external service or a long calculation. Write a log for a state change or a decision that a person can need later, such as "Created tenant 42 for new user 7". Do not write a log that only repeats a span, such as "Handling GetUser".

## Custom spans and events

`Telemetry.Trace` starts a child span of the current activity. `Telemetry.AddEvent` adds an event to the current activity. Both add the `code.function`, `code.file.path`, and `code.line.number` tags of the caller, so each span and each event leads to its code.

```csharp
using var activity = Telemetry.Trace([("app.tenant_id", tenantId)], "invoice.render");
// ... the work
Telemetry.AddEvent([("app.page_count", pages)], "invoice.paginated");

Telemetry.RecordError(activity, exception);
```

Use a stable dotted span name with no ids or data in it. `AddEvent` does nothing when no activity is current. In top-level statements, such as `Program.cs`, `code.function` is `<Main>$`. The file and the line are still correct.

## Metrics

The custom metrics are on the `Recall.Server` meter:

- `app.request.duration` - a histogram of the duration of each feature slice request, in seconds. `RequestTracingBehavior` records it. The tags are `app.request.name` (for example `GetUser.Query`) and, when the request throws, `error.type`.
- `app.errors` - a counter of server errors. `RecordError` increments it with the `exception.type` tag. A client error (4xx) does not increment it.

The host also collects these framework meters:

- ASP.NET Core, Kestrel, `System.Net.Http`, and the .NET runtime (from the instrumentation packages).
- `Polly`, for the resilience handlers on `HttpClient`. The meter has data only after an outgoing call.
- `Microsoft.EntityFrameworkCore`. The server only.
- `Microsoft.AspNetCore.Authentication` and `Microsoft.AspNetCore.Authorization`.

### The metrics of the removed BFF

The BFF had three metrics of its own. The BFF kept each session and refreshed its tokens at the identity provider, and ADR 0014 moved the sessions to the Server. No custom metric replaces the three. These signals answer the same questions:

| Removed metric | The signal now |
|---|---|
| `bff.sessions.started` | The log `Started web session {SessionId} for user {UserId}`, and the same log for a native session. `aspnetcore.authentication.sign_ins` does not count them, because the Server stores its own sessions. |
| `bff.sessions.ended`, with a reason | The columns `revoked_at` and `revoked_reason` of the Auth session row. A refresh token that comes back gives the log `Ended session {SessionId} because an old refresh token came back`. No log marks a logout or an expiry. |
| `bff.refresh.failures` | An Auth session keeps working when the identity provider is down, so a web session has no refresh that can fail. A failed native refresh shows as the request log of `POST /auth/token` with its 4xx status. |

`aspnetcore.authentication.authenticate.duration` counts each check of a session, and its tag `aspnetcore.authentication.result` tells `success`, `failure`, or `none`.

## Resource attributes

Traces, metrics, and logs share the same resource attributes: `service.name`, `service.version`, `service.instance.id`, `deployment.environment`, and `host.name`. Serilog exports logs with its own OTLP sink, not with the OpenTelemetry SDK. For this reason, `HostExtensions.ResourceAttributes` gives the one list to both. As a result, the dashboard shows one resource for all three signals.

The values in `OTEL_SERVICE_NAME` and `OTEL_RESOURCE_ATTRIBUTES` win over the defaults in the code. Aspire sets `service.name` to the resource name (`server`) and `service.instance.id` to the replica id. The dashboard finds the telemetry of a resource by these two values. Thus `dotnet aspire otel traces server` finds the traces of the server. With no such variables, as in a deployment that does not set them, `service.name` is the application name, for example `Recall.Server`.

## Feature slice spans

`RequestTracingBehavior` wraps each MediatR request in a span. The span name is the feature class plus the request type, for example `GetUser.Query`. You do not have to add code to a new slice to get this span or the `app.request.duration` metric.

The behavior tags an exception type as `error.type`. A client error does not mark the span as failed. A client error is an exception for which `DomainError.IsClientError` is true. These are the domain exceptions that each entrypoint maps to a client error, for example a 4xx response (see the error table in [backend.md](backend.md#error-responses)). All other exceptions mark the span with the `Error` status and increment `app.errors`.

## Caller tags

`CallerIdentityBehavior` resolves the caller before the handler runs. It then tags the feature slice span with the caller, and adds the caller to the log context of the handler. Thus each log that the handler writes has the caller too. A value that the caller does not have is not added. The behavior runs before `RequestValidationBehavior`, so a request that fails validation also has the caller tags.

| Span tag | Log property | Value |
|---|---|---|
| `app.caller.kind` | `CallerKind` | `user`, `delegated_agent`, `machine_agent`, `all_tenants_client`, `unknown_client`, `anonymous`, or `system` |
| `app.caller.subject` | `CallerSubject` | The `sub` claim of a user, also the user for whom a delegated agent acts |
| `app.caller.client_id` | `CallerClientId` | The client of an agent or of a machine client |
| `app.caller.agent_id` | `CallerAgentId` | The id of the registered agent |
| `app.caller.tenant_id` | `CallerTenantId` | The tenant of the caller |

The values are ids only. An MCP tool call can fail before its feature runs, so `McpCallTelemetry` also puts the caller tags on the span of the HTTP request of each tool call. The request log of `UseSerilogRequestLogging` runs outside the MediatR pipeline, so it does not have the caller properties. Use its trace id to find the feature slice span.

## Domain event handler spans

`AppDbContext.SaveChangesAsync` runs each domain event handler in a `domain_event.handle` span. The span is a child of the current activity, which is the feature slice span in a request. The tags are `app.domain_event.type` and `app.domain_event.handler`. They have the type names only, never the data of the event.

The handler span tags the type of an exception as `error.type`, the same as the feature slice span. A server error also marks the span with the `Error` status and adds the exception. The handler span writes no log and does not increment `app.errors`. The exception fails the save, and the code that called the save counts it one time. In a request, the feature slice span counts it.

## Named queries

Tag each EF query in a feature slice with `TagWithOperationCallSite`:

```csharp
var tenant = await dbContext.Tenants
    .TagWithOperationCallSite("tenants.get_by_id")
    .GetById(request.Id, cancellationToken);
```

The SQL span then has these properties:

- The name is `SQL: tenants.get_by_id`.
- `db.operation.name` is the operation name.
- `code.function`, `code.file.path`, and `code.line.number` give the C# call site.

Use a stable dotted name in the form `area.operation`. Never put an id or other data in the name. An untagged query keeps the default span name `sqlite.command`.

`PagedList.CreateAsync` runs two queries on the tagged query that it gets: the count and the page. It tags the count query with `TagAsCountQuery`, which adds the `count` suffix to the name. Thus the count of `users.list` is `SQL: users.list.count`, and the page is `SQL: users.list`.

A tenant delete loads the rows of each tenant-owned table with its own query, in `Databases/TenantOwnedRows.cs`. Each query is named `tenants.delete.load_{table}`, for example `SQL: tenants.delete.load_agent_permissions`. The call site of each is `TenantOwnedRows.LoadRowsAsync`.

## Queries with no parent span

`SqlSpanInterceptor` makes a query span only when a recorded activity is current. This rule drops two types of orphan traces:

- Queries that run with no activity at all.
- Queries under a request that the ASP.NET Core filter dropped, such as `/health`.

As a result, background work that does queries must start its own trace with `TraceRoot`.

## TraceRoot

`Telemetry.TraceRoot` starts a span that is the root of a new trace. The span links back to the activity that was current before it. Use it for work that has no request, or for work that is much larger than the request that started it.

```csharp
using var trace = Telemetry.TraceRoot(
    [("app.db_context", typeof(TDbContext).Name)],
    "database.migrate");
```

`MigrationHostedService` uses this helper, so a startup migration is one `database.migrate` trace. The scope puts the previous activity back when you dispose it. The span gets the same `code.*` tags as a tagged query.

## TraceContext

`TraceContext` carries a trace context in data, for a hop where no `traceparent` header is available. Examples are a message payload or a redirect.

```csharp
var captured = Telemetry.CaptureCurrentTraceContext();
// ... later, on the other side of the hop
ActivityLink[] links = Telemetry.TryParseTraceContext(captured, out var parent) ? [new(parent)] : [];
using var trace = Telemetry.TraceRoot([], "work.run", links);
```

`TryParseTraceContext` returns false for a null, empty, or malformed context. The receiver then continues with no link.

## Logs

Serilog is the logging provider. The file sink writes each log to `logs/` beside the database. The OTLP sink also sends logs to the Aspire dashboard when `OTEL_EXPORTER_OTLP_ENDPOINT` is set. Each sink must stay inside the `AddSerilog` callback in `HostExtensions.cs`. A logger that you assign to `Log.Logger` in a different location is replaced when the host resolves `ILoggerFactory`, so its sinks stop there.

### Call site

Write each log through `Here()`:

```csharp
private readonly ILogger _logger = Log.ForContext<Handler>();

_logger.Here().Information("Created tenant {TenantId} for new user {UserId}", tenant.Id, user.Id);
```

`Here()` adds these properties to the log event:

- `CallSite` - the short form, for example `MigrationHostedService.cs:StartAsync@31`.
- `code.function`, `code.file.path`, and `code.line.number` - the same names as on a span.

Log only ids, names of operations, and counts. The [privacy rule](#privacy-rule) applies to each log.

### Console output

In Development, the console uses a `Serilog.Expressions` template. Each line shows the time, the level, `SourceContext`, `CallSite`, and the trace id. The message is on the next line. In other environments, the console writes `CompactJsonFormatter` JSON. `appsettings.json` gives the formatter as an object, so that `appsettings.Development.json` can replace its type.

The console has no queue. A log call writes to standard output on the thread of the request. When the reader of the pipe stops reading and the pipe is full, each request that writes a log waits until the reader reads again. A probe of `/alive` writes no log, so it stays 200 during this time, and a host that probes only `/alive` does not see the fault. A run with a stopped pipe showed this: requests stopped at 343 of 2000, and `/alive` gave 200 in 0.002 s. That run was a process on a pipe, not a container, so the behavior of a Docker log driver was not measured. The console also has no total limit: a run wrote 292 MB in 25.6 s, and the log driver of the host decides what stays.

`Serilog:WriteTo` holds the console inside `CappedText`, and that setting is the only thing that gives the console its cap. Do not replace it, and do not add a sink to it, unless the new sink is also inside `CappedText`. Three settings show the result of three calls with a text of 20 MB:

| Setting | Result on the console |
|---|---|
| `Serilog__WriteTo__0__Name=Console` | No cap and no JSON. The longest line was 20,000,118 bytes, and the total was 60 MB. |
| `Serilog__WriteTo__1__Name=Console` | The capped console stays, and a second console with no cap and no JSON writes each line again. |
| `Serilog__WriteTo__0__Name=NoSuchSink` | Only the start line, and no warning. Each later log is lost on the console. |

In each of these runs, the longest line of the log file stayed at 16,968 bytes. `HostExtensions.cs` sets the cap of the file and of the OTLP logs, so these settings do not change it.

### Request logs

`UseSerilogRequestLogging` writes one log for each request. `RequestLogLevel` sets its level:

- An exception or a status code of 500 or more gives `Error`.
- A health probe (`/health` or `/alive`) gives `Verbose`, so the default minimum level drops it.
- A health probe that the caller leaves during a check gives `Verbose`. In the server, `CallerCancellationHealthCheck` ends each check with the cancellation, so the exception handler gives 499, not 503. A check that runs past its own timeout still reports `Unhealthy` at `Error`.
- All other requests give `Information`.

## Files

A person who runs Recall on a NAS has no log service, so the server keeps its own files. The folders are beside the database, so `dirname(database)/logs` and `dirname(database)/telemetry`. In the image the database is `/data/recall.db`, so the folders are `/data/logs` and `/data/telemetry`. A process run finds them in the same way. An in-memory database writes no files.

| File | Content | Limit of one file | Files kept | Days kept |
|---|---|---|---|---|
| `logs/recall-<date>.jsonl` | One log in each line, in the Serilog compact JSON format, with the trace id in `@tr` and the span id in `@sp` | 10 MiB | 20 | 14 |
| `telemetry/traces-<date>.jsonl` | One span in each line | 10 MiB | 20 | 14 |
| `telemetry/metrics-<date>.jsonl` | One point of a metric in each line, each hour | 4 MiB | 15 | 14 |

The most that these limits hold is about 460 MiB: 200 MiB of logs, 200 MiB of traces, and 60 MiB of metrics. The section `FileTelemetry` in `appsettings.json` sets them. Set `FileTelemetry__Logs__FilesKept`, for example, to change one in an environment variable. A file passes its size limit by at most one line, and then the next line opens a new file with a number after the date.

A string in telemetry has at most 16,384 UTF-16 code units (`TelemetryText.MaxLength`). A longer string keeps its start and ends with `...[cut by Recall, length <length>]`. The longest real values that a run gave are stack traces of 8,081 and 5,558 code units, and a caller controls many other strings, such as an MCP method, `clientInfo`, or an argument that an exception message names. The first logger of a start runs before the settings load and has no cap. It writes the start line, a refusal, and a startup failure, and a refusal has its own cap. `SpanTextCapProcessor` runs before each trace exporter, so it holds for the trace file and OTLP. It cuts the name, the status, and the string tags of a span, and the text of each exception event. `LogTextCapSink` wraps the file sink and the OTLP sink in `HostExtensions.cs`, and the console through `Serilog:WriteTo`, so the console keeps it only while that setting stands (see [Console output](#console-output)). It cuts each string value of a log event, its message template, and its exception. An exception that it cuts reaches OTLP with the type `CutException`, and the first words of its text name the real type. `JsonLine` also cuts each tag that is not a string, when it writes the tag to a file, so this holds for the files only.

These caps do not reach each string. A log event keeps the keys of a dictionary, its property names, and a value that is not a string, such as a `Uri`. A span keeps its tag keys, its event names, its trace state, a second value that `AddTag` adds for a key that it already has, and on OTLP the tags of an event that is not an exception. The tests and the attack runs found no way for a client to put a long text in these places, but they do not prove that no way exists. So the limit of a file holds for each client call that they sent, and a new tag or log property that takes text from a caller must be a string value.

A `FileSizeBytes`, `FilesKept`, or `MetricExportSeconds` under 1, a `DaysKept` under 0, a `DaysKept` over 3650, a `MetricExportSeconds` over 86400, or a value that is not a whole number in the range of its type, stops the server at startup with one line that names the setting. A `DaysKept` of 0 keeps only the file that just opened, so each open removes each other file of that signal. The age limit acts when a file opens, so at a start, at the first line of each day, and when a file reaches its size limit.

With a database file, a section with only some of these settings also stops the server at startup with one line that names the first missing setting. With no `FileTelemetry` section, or with an empty one, the server starts, writes no log file and no telemetry file, and gives no warning. The console and OTLP do not change, and a support bundle then has no logs and no telemetry.

`Days kept` is the most that a file stays, and the number of files can remove a file sooner. A request costs about 10.8 KB of traces, so 20 files hold about 19,000 requests. A request costs about 590 bytes of logs, so 20 files hold about 350,000 requests. A flood of requests thus pushes out old logs and old traces, and 14 days holds only on a quiet server. The metrics do hold 14 days. Each day opens a new file, and 15 files hold each file of 14 days. One export is about 9 KB on an idle server and about 48 KB after a request to each route, so one day of exports uses about 1.15 MB of the 4 MiB of its file.

A disk can sleep on an idle server. In 10 minutes with no request, one write reached a file: SQLite moved its write-ahead log into `recall.db` (196,608 bytes) when its last connection closed. An export each 60 seconds gave 10 more writes (91,494 bytes) in the same time, so `FileTelemetry:MetricExportSeconds` is 3600. The server exports metrics each hour, at a stop, and for a support bundle. A crash thus loses at most one hour of metric points. The `auth.sweep` of each hour also opens the database and writes a span, so an idle server writes to the disk at least once in each hour. A probe of `/alive` writes to no file. A probe of `/health` proves that the file store takes a write, at most once in 30 seconds, so a host with a disk that must sleep probes `/alive`.

The server makes these folders with owner-only modes. For these files, a logging call and a span give their data to a queue and return, so a slow or full disk does not hold a request. This is not true for the console, which has no queue (see [Console output](#console-output)). When the disk cannot take a line, or when the queue of 10,000 logs is full, the line goes, and the console shows one warning for each file in each 30 minutes. See [troubleshooting.md](troubleshooting.md#the-log-files-stop). The console and OTLP do not change, and the two OTLP exports run beside the files.

No package fits these files, so `SpanFileExporter` and `MetricFileExporter` are ours. Each writes one JSON line through `JsonLinesFile`, and Serilog.Sinks.File does the rotation. A throw in a span export would end the process, because the batch thread has no catch. A throw in a metric export would lose each metric of that export with no warning, because the metric reader catches it. Each exporter thus catches a throw for each line, such as a tag whose `ToString` throws or an out-of-memory error. It loses that line, keeps the other lines of the batch, reports a failed export, and shows the warning of the file. .NET cannot catch a failure of the runtime itself. A file that cannot flush when it closes, on a full disk or a lost volume, shows one line on the console, and the stop still exits with 0.

The [support bundle](troubleshooting.md#send-a-support-bundle) gives these files to the Owner in one zip. The Owner is the oldest linked Admin of the oldest live tenant. When that Admin loses the role, the next oldest linked Admin becomes the Owner, and the former Owner gets a refusal. Its `settings.json` shows only the sections that `RedactedSettings` names as sections that Recall reads, so a variable of the host stays out. It hides each value unless `RedactedSettings` lists the setting. A new setting thus starts hidden.

## Background jobs

`AddBackWaveInstrumentation` sends the spans and the metrics of BackWave to the same places. Each attempt of a job is a span named `process <queue>`, with the wire name of the job in `messaging.destination.template` and the job ID in `messaging.message.id`. The span of a poll that claimed no job goes to no exporter, so an idle server writes no span. See [background jobs](backend/background-jobs.md).

## Tests

- Unit tests use an `ActivityListener` and filter spans by the trace id of the test. Test classes run in parallel, so the listener sees spans from other tests. See `tests/Recall.UnitTests/Resources/TelemetryTests.cs`.
- Unit tests for metrics use a `MeterListener`. See `tests/Recall.UnitTests/Resources/RequestTracingBehaviorTests.cs`.
- Functional tests use the OpenTelemetry in-memory exporter. `TestBase.ExportedSpans()` returns the spans that the current test exported. `TestBase.AllExportedSpans()` also returns startup spans, such as `database.migrate`. See `tests/Recall.FunctionalTests/FunctionalTests/Telemetry/TracingTests.cs`.
- `TestingWebApplicationFactory.ExportedMetrics` collects the exported metrics. Call `MeterProvider.ForceFlush()`, then read them with `ExportedMetrics.Snapshot()`. See `tests/Recall.FunctionalTests/FunctionalTests/Telemetry/MetricsTests.cs`.
- `OtlpCollector` takes each OTLP export of the `AuthHosts.Bundle` host over gRPC and keeps its bytes. A string in protobuf is plain UTF-8, so `Holds` finds a text with no parser. `AuthHosts` adds the logger of that host to the static `Log.Logger`, because a handler writes through the static logger, and one process has only one. See `tests/Recall.FunctionalTests/FunctionalTests/Support/SupportBundleTests.JournalText.cs`.

## Read telemetry as an agent

The `aspire-telemetry` skill gives the commands that query logs, spans, and traces, and the steps from a log or a span to the code.
