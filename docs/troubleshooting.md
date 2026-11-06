# Troubleshooting

This page is for a person who runs Recall on their own machine, with no log service. The server keeps its logs and its telemetry in files beside its database, so you can read each failure there. For the file formats and the limits, see [Files](telemetry.md#files) in the telemetry doc.

In the image, the files are in these folders:

| Folder | Content |
|---|---|
| `/data/logs` | The logs, in `recall-<date>.jsonl` |
| `/data/telemetry` | The spans, in `traces-<date>.jsonl`, and the metrics, in `metrics-<date>.jsonl` |

In a process run, the folders are beside the database that `ConnectionStrings__appdb` names. The files have owner-only modes, so read them as the account that runs the server.

## Find a failed request

1. Find the log of the request. The server writes one log for each request, and a status of 500 or more has the level `Error`.

   ```bash
   grep '"@l":"Error"' /data/logs/recall-*.jsonl
   grep '"RequestPath":"/api/v1/users"' /data/logs/recall-*.jsonl
   ```

   Each line has the status in `StatusCode` and the trace id in `@tr`. A problem response from the server has the same trace id in its `traceId` field, between the two dashes after `00`.

2. Find each log and each span of that trace.

   ```bash
   grep '"@tr":"<trace id>"' /data/logs/recall-*.jsonl
   grep '"traceId":"<trace id>"' /data/telemetry/traces-*.jsonl
   ```

   Each span line has `name`, `durationMs`, `status`, `parentSpanId`, and `tags`. The span with the `Error` status and with no failed child is the cause. A span keeps its data until the batch writes it, so a span of the last 5 seconds may not be there yet.

## The server stops at startup

The server writes the cause to its console, and not to a file, because the data folder may not take a file. Run `docker compose logs` to read the console of the image.

| The line | The cause | What to do |
|---|---|---|
| `The data directory <path> is not writable...` | The account of the server cannot write to the data folder, or the volume is read-only. | Mount the volume for writing, and give its owner to the account of the server. Then start the server again. |
| `The setting FileTelemetry:<name> is <value>, and its least value is <least>...` | A limit of the log and telemetry files is too small. | Correct the setting. Then start the server again. |

## The file store hangs

The server keeps running when a write to the file store hangs. It logs `did not finish` at the level `Error`. The disk or the network share is the likely cause.

Check the disk. When the write finishes, the server logs `takes writes again`, and you do not need to restart it.

## The disk is full

The server keeps its logs and its telemetry to about 460 MiB, and a full disk does not stop a request by itself. The database and the file store still need room, so a save fails with an error at the caller. Do these steps:

1. Run `df -h /data` and `du -sh /data/*` to find what holds the space.
2. Make room on the volume. You can delete old files in `/data/logs` and `/data/telemetry` while the server runs. Do not delete `recall.db`, its `-wal` file, or the `keys` folder.
3. If `/health` answers 503 and says `The background jobs stopped`, restart the server. The journal does not need a restart, but no background job runs until then. See [The background jobs stopped](#the-background-jobs-stopped).

## The background jobs stopped

A store fault that is not transient, such as a full disk, stops the background jobs and leaves the journal up. `/health` answers 503, and its body starts with `Unhealthy` and says `The background jobs stopped`. The log has one line at the level `Fatal` with the same text. A person can still read and write in the journal. Text that waits for a job, such as a transcript, waits until the jobs run again.

The server cannot restart the jobs by itself. Do these steps:

1. Fix the cause. For a full disk, see [The disk is full](#the-disk-is-full).
2. Restart the server with `docker compose restart`. A job that was running runs again.
3. Open `/health`. It answers 200 when the jobs run.
4. If `/health` answers 503 again and names `SqliteException` or `InvalidOperationException`, the cause can be a disk that is still full, a damaged file, or damaged job tables. Check the first two first. If the disk has room and `docker compose logs` does not show `database disk image is malformed`, do these steps on the host. The image has no `sqlite3`, so the host needs it, and the steps need the folder of the `/data` volume. Find this folder with `docker volume inspect`.
   1. Stop the server with `docker compose stop`.
   2. Copy `recall.db`, `recall.db-wal`, and `recall.db-shm` to a safe place.
   3. In the folder of the volume, run `sqlite3 recall.db "DELETE FROM backwave_schema_version"`.
   4. Start the server with `docker compose start`. BackWave makes each missing job table again and keeps the rows of the journal.

A restart during a long job costs one attempt of that job. A job at its last attempt goes to the dead letters. The Owner requeues it at `/backwave/failures`.

## The log files stop

When a log or telemetry file cannot take a line, the console shows a warning with the text `A file sink failed`, and with the fields `File` and `Cause`. It shows one warning for each file in each 30 minutes. The console keeps each log during the fault, so `docker compose logs` stays complete, if Docker keeps its own logs on a volume with room. The file loses the lines of the fault.

- A write that fails on a full disk stops when the disk has room again. The next line goes to the file.
- A file that cannot open, for example at a roll or at the first line of a day, stays silent for up to 30 minutes from that failure. This is also true when the disk has room again before that time.

To get the files back at once, start the server again. The server keeps each file that it wrote before the fault.

## Send a support bundle

The support bundle is one zip with the log and telemetry files, the version, and the settings with the secrets hidden. It holds no journal text: a job records the type and the stack frames of a thrown exception and no message, unless the type states that its text is safe. Only the Owner can download it. The Owner is the oldest linked Admin of the oldest live tenant. When that Admin loses the role, the next oldest linked Admin becomes the Owner.

1. Sign in as the Owner.
2. Open the **About** page.
3. Select **Download Support Bundle**.

The zip has these entries:

| Entry | Content |
|---|---|
| `logs/` | Each log file that holds a line |
| `telemetry/` | Each trace file and metric file that holds a line |
| `version.json` | The version, the runtime, the operating system, the environment, and each file or folder that the server could not read |
| `settings.json` | The name of each setting that Recall reads. Only a value that cannot be a secret is shown, and each other value is `[redacted]` |

Another Admin gets the message `Only the Owner of this install can download the support bundle.`
