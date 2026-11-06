# Job dashboard

The Owner opens `/backwave` and sees the BackWave console, which shows the health job of each start of the host. Each other caller gets a 403 with an empty body.

## Sub-features

- `overview` shows the BackWave console, with the sidebar `Overview`, `Executing now`, `Jobs`, `Queues`, `Failures`, `Observers`, and `Recurring Schedules`.
- `jobs` shows a row with the wire name `system-health`, the queue `system`, and the state `Succeeded` at `/backwave/jobs`.
- `refused` gives a 403 to a caller with no session and to each signed-in user who is not the Owner.

## How to get to it (user POV)

- Open `/backwave` directly. The SPA has no link to it.

## Driving it with browse.mjs

Preconditions:

- `doctor.sh` exits with 0.

Run:

```bash
node .agents/skills/verify-recall/scripts/browse.mjs .agents/skills/verify-recall/recipes/job-dashboard.mjs
```

## Proof

- A screenshot and an ARIA snapshot of `/backwave/jobs` for the Owner.
- The status of `/backwave` for a new browser context with no session.
