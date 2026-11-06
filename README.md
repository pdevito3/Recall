# Recall

Recall is a self-hosted journal. A person opens the phone, dictates, and the entry is captured with no friction. Audio is durable before any transcription starts, and no failure can lose words.

Recall started from [FullstackTemplate](https://github.com/pdevito3/FullstackTemplate) at commit `e1cffd29052419331957407033c38ec71f62800d`. The code is still the stack of the template. [ADRs 0013 to 0016](docs/adr/) give the target shape, and [CONTEXT.md](CONTEXT.md) gives the language.

The stack today has:
- **React 19** frontend with TanStack Router and React Query
- **Backend-For-Frontend (BFF)** pattern with YARP and OIDC, and server-side sessions in SQLite
- **Configurable authentication** - one setting selects the auth provider, and a new provider is one folder in the AppHost
- **Multi-tenant architecture** with data isolation
- **OpenTelemetry** observability built-in
- **.NET 10** with Aspire 13.6

## What You Get

```
Recall/
├── Recall.AppHost/     # Aspire orchestration (start here)
├── Recall.Server/      # Backend API (.NET 10, CQRS, MediatR)
├── Recall.Bff/         # BFF proxy (YARP, OIDC)
├── frontend/           # React SPA (React 19, TanStack, Tailwind)
├── tests/              # Test projects
├── docs/               # Development guides (backend, telemetry, auth, ...)
└── AGENTS.md           # Agent guide & index of docs
```

### Backend Features
- **CQRS with MediatR** - Vertical slice architecture
- **Rich Domain Entities** - DDD patterns with value objects
- **Multi-tenancy** - Automatic tenant isolation
- **Audit Log and Soft Delete** - An append-only log of each change, and soft delete built-in
- **API Versioning** - URL segment versioning (`/api/v1/...`)
- **OpenTelemetry** - Distributed tracing and metrics
- **SQLite** - With EF Core and migrations. The app data is in `recall.db`, and the BFF sessions are in `bff.db`

### Frontend Features
- **TanStack Router** - File-based routing with type safety
- **TanStack Query** - Server state management
- **TanStack Table** - Data tables with sorting/filtering
- **30+ UI Components** - Based on shadcn/ui and React Aria Components, animated with Animate UI
- **Filter Builder** - Advanced filtering with QueryKit integration
- **Theme System** - Light/dark/system mode
- **React Hook Form + Zod** - Form handling with validation

## Running the Application

```bash
cd Recall.AppHost
dotnet run
```

Or use the Aspire CLI:
```bash
cd Recall.AppHost
aspire run
```

This starts:
- **Aspire Dashboard** - View logs, traces, and metrics
- **Backend API** - .NET API server
- **BFF Proxy** - Handles auth and proxies to API
- **React Dev Server** - Vite with hot reload
- **Auth Provider** - the provider that the `AuthProvider` setting selects

The UI starts after the BFF and the API are healthy, and the BFF starts after the auth provider is healthy. Until then, the dashboard shows the UI as `Waiting`.

### Ports

A normal run uses the same ports at each start. All of them are in the block 24700 to 24799, which no common tool uses as a default. Each group of ten ports has one part of the stack:

| Port | Resource | Endpoint |
|---|---|---|
| 24700 | Aspire dashboard | https |
| 24701 | Aspire dashboard | http |
| 24702 | Aspire dashboard | OTLP |
| 24703 | Aspire dashboard | resource service |
| 24704 | Aspire dashboard | MCP |
| 24705 | `tests` (the Labcoat test UI) | http |
| 24710 | `webfrontend` (the app URL) | http |
| 24711 | `bff` | https |
| 24712 | `bff` | http |
| 24713 | `server` | https |
| 24714 | `server` | http |
| 24715 | `server` | grpc |
| 24730 | `keycloak` | http |
| 24731 | `keycloak-postgres` | tcp |
| 24740 | `zerofactor` | http |
| 24750 | `fusionauth` | http |
| 24751 | `fusionauth-postgres` | tcp |
| 24760 | `authentik` | http |
| 24761 | `authentik-postgres` | tcp |
| 24770 | `cognito` | http |

The dashboard ports are in `Recall.AppHost/Properties/launchSettings.json`. The server ports are in `Kestrel:Endpoints` of `Recall.Server/appsettings.Development.json`, and the BFF ports are in `Recall.Bff/Properties/launchSettings.json`. The other ports are in `Recall.AppHost/AppHost.cs` and in the class of each auth provider. A new resource takes a port in the block. A new auth provider takes the next free group of ten. `PortTableTests` fails when an endpoint of the AppHost has no row in this table or has a different port.

The provider seeds get the app URLs from the AppHost, so the redirect URIs agree with these ports. Keycloak and FusionAuth seed their database only when it is empty. If a provider database is from a run with other ports, delete its volume once (`docker volume ls`, then `docker volume rm <volume>`).

### Run several stacks at the same time

To run a stack in each git worktree, start each one in isolated mode:

```bash
dotnet tool restore   # once: installs the pinned Aspire CLI as `dotnet aspire`
dotnet aspire run --isolated --apphost Recall.AppHost
```

An isolated run gives each endpoint a random port and uses a copy of the user secrets. It also starts with empty data files in a new temp directory. A normal run uses the fixed ports of the table above and keeps its data files in `.data` of the checkout. `RECALL_DATA_DIR` sets another directory.

## Auth Provider

To select the auth provider, add a provider, or find the test users, see [docs/authentication.md](docs/authentication.md).

## Authentication Flow

```
┌──────────┐     ┌─────────┐     ┌───────────────────┐
│  React   │────►│   BFF   │────►│   Auth Provider   │
│   App    │◄────│ (OIDC)  │◄────│  (e.g. Keycloak)  │
└──────────┘     └─────────┘     └───────────────────┘
     │                │
     │   Cookie       │  JWT Token
     │                ▼
     │          ┌─────────┐
     └─────────►│  API    │
       via BFF  │ Server  │
                └─────────┘
```

1. The React app has no session, so it navigates to `/bff/login`
2. BFF redirects to identity provider
3. User authenticates with provider
4. Provider redirects back to `/signin-oidc`
5. BFF creates secure `__Host-bff` cookie
6. React app calls `/bff/user` to get claims
7. API calls go through BFF at `/api/v1/*`

## Development with Claude Code

The repository has these guides for Claude Code:

- **`AGENTS.md`** - Main agent guide with the architecture and an index of `docs/`
- **`docs/`** - Guides for the backend, telemetry, multi-tenancy, auth, and frontend
- **`docs/backend/`** - Backend pattern rules: entities, features, DTOs, endpoints (HTTP, gRPC, MCP), domain events, and EF configurations
- **`docs/frontend/`** - Frontend pattern rules: server state, client state, and code style
- **`.claude/skills/`** - Automated scaffolding skills

## License

MIT
