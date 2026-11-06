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

## The file store hangs

The server keeps running when a write to the file store hangs. It logs `did not finish` at the level `Error`. The disk or the network share is the likely cause.

Check the disk. When the write finishes, the server logs `takes writes again`, and you do not need to restart it.

## The disk is full

The server keeps its logs and its telemetry to about 450 MiB, and a full disk does not stop a request by itself. The database and the file store still need room, so a save fails with an error at the caller. Do these steps:

1. Run `df -h /data` and `du -sh /data/*` to find what holds the space.
2. Make room on the volume. You can delete old files in `/data/logs` and `/data/telemetry` while the server runs. Do not delete `recall.db`, its `-wal` file, or the `keys` folder.

## Send a support bundle

The support bundle is one zip with the log and telemetry files, the version, and the settings with the secrets hidden. It holds no journal text. Only the Owner can download it.

1. Sign in as the Owner.
2. Open the **About** page.
3. Select **Download Support Bundle**.

The zip has these entries:

| Entry | Content |
|---|---|
| `logs/` | Each log file |
| `telemetry/` | Each trace file and metric file |
| `version.json` | The version, the runtime, the operating system, the environment, and each file or folder that the server could not read |
| `settings.json` | The name of each setting. Only a value that cannot be a secret is shown, and each other value is `[redacted]` |

Another Admin gets the message `Only the Owner of this install can download the support bundle.`
