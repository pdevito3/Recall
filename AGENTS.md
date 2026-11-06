# AGENTS.md

Guidance for coding agents that work in this repository.

## Overview

Recall is a self-hosted journal. A person opens the phone, dictates, and the entry is captured with no friction. Read [CONTEXT.md](CONTEXT.md) for its language and [docs/adr/](docs/adr/) for its decisions. ADRs 0013 to 0016 give the target shape, and the code moves to it PR by PR.

Today this is a .NET Aspire application with a React frontend. The Server runs the login and owns the sessions (ADR 0014). It has multi-tenancy, a CQRS architecture, and a configurable auth provider. The `AuthProvider` setting of the AppHost selects it for a local run. See [docs/authentication.md](docs/authentication.md).

```
┌─────────────────────────────────────────────────────────────┐
│                    Aspire AppHost                           │
│                 (Orchestration Layer)                       │
└─────────────────────────────────────────────────────────────┘
         │                                         │
    ┌───────────┐                            ┌──────────┐
    │  Server   │◄──────────────────────────►│ Frontend │
    │(API, auth)│    (cookie, X-CSRF: 1)     │  (React) │
    └───────────┘                            └──────────┘
       │     │
┌────────┐ ┌──────────────┐
│ SQLite │ │Auth Provider │
└────────┘ └──────────────┘
```

| Project | Purpose |
|---------|---------|
| `Recall.AppHost` | Aspire orchestration. Defines all resources in `AppHost.cs` |
| `Recall.Server` | Backend API and login (.NET 10, app sessions, CQRS, multi-tenant). Serves the SPA in production |
| `frontend` | React SPA (React 19, TanStack Router, TanStack Query, Tailwind v4) |
| `tests/` | Unit, integration, and functional tests |

## Docs

Read the doc for an area before you change code in that area.

| Doc | Content |
|-----|---------|
| [docs/backend.md](docs/backend.md) | Domain structure, new features, DI, API versioning, error responses, database, file store, tests |
| [docs/backend/](docs/backend/) | Rules for entities, features, DTOs and mappers, endpoints (HTTP, MCP), concurrency (a version, a run again, or a lock), domain events, EF configurations, feature telemetry, and the audit log |
| [docs/backend/source-generators.md](docs/backend/source-generators.md) | When to use a source generator and when reflection is allowed |
| [docs/telemetry.md](docs/telemetry.md) | Traces, metrics, logs, `Trace`, `AddEvent`, `Here()`, `TraceRoot`, `TraceContext`, named queries |
| [docs/troubleshooting.md](docs/troubleshooting.md) | The log and telemetry files of an install: find a failed request, read a startup failure, send a support bundle |
| [docs/messages.md](docs/messages.md) | The text of error messages, alerts, toasts, and labels |
| [docs/multi-tenancy.md](docs/multi-tenancy.md) | Tenant isolation |
| [docs/authentication.md](docs/authentication.md) | Web and native login, sessions, origins, auth providers, test users |
| [docs/install.md](docs/install.md) | The container, the compose files, `/data`, HTTPS, the health probe, backup, and upgrade |
| [docs/frontend.md](docs/frontend.md) | Frontend structure, API client, query hooks, routes, forms |
| [docs/frontend/](docs/frontend/) | Rules for server state, client state, code style, and tests |
| [docs/specs/](docs/specs/) | The specs of a domain or an effort. Start a new spec from [TEMPLATE.md](docs/specs/TEMPLATE.md): each rule has an ID, and a test names the ID |
| [docs/ios-api-changes.md](docs/ios-api-changes.md) | Each route that the POC iOS client calls, and its Recall route or the unit that builds it |
| [docs/csharp-repl.md](docs/csharp-repl.md) | CSharpRepl in the running server: the hook wiring and the Development-only guards |
| [docs/adr/0017-template-fixes-by-cherry-pick.md](docs/adr/0017-template-fixes-by-cherry-pick.md) | How Recall takes template fixes, and the template commit that Recall started from |
| [docs/template-updates.md](docs/template-updates.md) | The merge script of the template. Recall does not use it by default. See ADR 0017 |

The pattern rules are in `docs/backend/` and `docs/frontend/`. Before you write code in an area, read its rule doc. Obey the rules in it.

To add a Server feature, use the `backend-feature` skill. To add the SPA pages of a feature (a list, a detail page, a create or edit form, or a search picker), use the `frontend-feature` skill.

## Run the application

The Aspire CLI is a local tool in `dotnet-tools.json`. Run `dotnet tool restore` once, then use `dotnet aspire`.

```bash
dotnet aspire start --isolated --non-interactive --apphost Recall.AppHost
```

This command starts the Aspire dashboard, the server, the Vite dev server, and the auth provider in the background. `--isolated` gives the stack random ports and its own empty database, so that each worktree can run a stack at the same time. The `aspire-cli` skill gives the commands to wait for, inspect, restart, and stop the stack.

1. Before you make changes, start the stack and make sure that all resources are healthy.
2. If you change `AppHost.cs`, restart the stack. Other code changes hot-reload.
3. If a resource fails, read its logs and traces before you change code. The `aspire-telemetry` skill gives the commands.
4. To run C# in the live server (inspect DI or data, send a MediatR request, patch a method), use the `csharprepl` skill.

## Build and test

```bash
dotnet tool restore
dotnet build Recall.slnx
dotnet test --solution Recall.slnx

cd frontend
pnpm install
pnpm build
pnpm lint
pnpm test
pnpm test:e2e
```

## Rules

- Put each entrypoint (HTTP, MCP) in the folder of its feature, not in a separate project. Each entrypoint is optional. Add only the entrypoints that the callers of the feature need. See [docs/backend/endpoints.md](docs/backend/endpoints.md).
- Use pnpm for the frontend. Do not use npm.
- Do not run Prettier. This repo has no Prettier config, so Prettier rewrites whole files. `.claude/settings.json` refuses the command in a Claude Code session that starts at the repo root. Keep the style of the file that you edit, and run `pnpm lint`.
- Put all NuGet versions in `Directory.Packages.props`.
- Warnings fail the build: `TreatWarningsAsErrors` in `Directory.Build.props` and `--max-warnings=0` in `pnpm lint`. Correct the cause of a warning. To accept a .NET warning, add its code to `NoWarn` with a comment that gives the reason. The C# style rules are in `.editorconfig`, and `EnforceCodeStyleInBuild` runs them in the build, so a style break also fails it.
- Use `docker compose` with a space. Do not use `docker-compose`.
- Do not install the Aspire workload. It is obsolete.
- Do not use persistent containers early in development. They keep state between restarts.
- Do not edit generated files, such as `frontend/src/routeTree.gen.ts`, `frontend/src/api/generated*/`, `Recall.Server/openapi/`, and the EF migration snapshots.
- If you change the Server API, use the `api-contract` skill.

## Aspire

- Before you add a resource, use the `list integrations` tool. Use the integration version that matches `Aspire.AppHost.Sdk`.
- Get the docs for an integration with the `get integration docs` tool, then obey them.
- To update Aspire, run `aspire update`. Then update the other packages for compatibility.
- Prefer the official docs: https://aspire.dev, https://learn.microsoft.com/dotnet/aspire, and https://nuget.org.

## MCP tools

- **Aspire MCP**: resources, console logs, structured logs, traces, and integrations.
- **Playwright MCP**: browser tests. Get the endpoint URLs from the `list resources` tool.
