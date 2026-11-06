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
| `The setting FileTelemetry:<name> is <value>, and its largest value is <most>...` | A limit of the log and telemetry files is too large. | Correct the setting. Then start the server again. |
| `Failed to convert configuration value '<value>' at 'FileTelemetry:<name>'... Correct it, and start the server again.` | A limit of the log and telemetry files is not a whole number in the range of its type. | Correct the setting. Then start the server again. |

## The file store hangs

The server keeps running when a write to the file store hangs. It logs `did not finish` at the level `Error`. The disk or the network share is the likely cause.

Check the disk. When the write finishes, the server logs `takes writes again`, and you do not need to restart it.

## The disk is full

The server keeps its logs and its telemetry to about 460 MiB, and a full disk does not stop a request by itself. The database and the file store still need room, so a save fails with an error at the caller. Do these steps:

1. Run `df -h /data` and `du -sh /data/*` to find what holds the space.
2. Make room on the volume. You can delete old files in `/data/logs` and `/data/telemetry` while the server runs. Do not delete `recall.db`, its `-wal` file, or the `keys` folder.

Do not delete the current file of a signal, which is the newest file of that signal, while the server runs. The server keeps it open, so its room stays in use. Each next line of that signal goes to the deleted file and is lost, with no warning, until the file rolls or the server starts again. To remove a current file, stop the server first.

A stop on a full disk exits with 0. If a file cannot write the lines that it still holds, the console shows `The file <path> did not close at the stop`, and those lines are lost. The console keeps each log. The database is not part of these files.

## The log files stop

When a log or telemetry file cannot take a line, the console shows a warning with the text `A file sink failed`, and with the fields `File` and `Cause`. It shows one warning for each file in each 30 minutes. The console keeps each log during the fault, so `docker compose logs` stays complete, if Docker keeps its own logs on a volume with room. The file loses the lines of the fault.

- A write that fails on a full disk stops when the disk has room again. The next line goes to the file.
- A file that cannot open, for example at a roll or at the first line of a day, stays silent for up to 30 minutes from that failure. This is also true when the disk has room again before that time.

To get the files back at once, start the server again. The server keeps each file that it wrote before the fault.

## Send a support bundle

The support bundle is one zip with the log and telemetry files, the version, and the settings with the secrets hidden. It holds no journal text. Only the Owner can download it. The Owner is the oldest linked Admin of the oldest live tenant. When that Admin loses the role, the next oldest linked Admin becomes the Owner.

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
