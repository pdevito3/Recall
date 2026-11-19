# Recall

> [!WARNING]
> **Recall is in alpha development. Do not use it in production.** The data model, the API, and the install steps change from one commit to the next, and no release exists yet. An upgrade can need a manual step, and it can lose data. Keep a backup of anything that you write in Recall.

Recall is a self-hosted journal for the voice. You open Recall on your phone, talk, and your words become a journal entry. You run Recall on your own machine, so the audio and the text stay on your own disk.

## What Recall does

Each time that you journal, Recall makes a **Session**. You can dictate or type into it. Recall stores your audio before it starts to transcribe, so a lost connection or a crash does not lose your words. The words that you said are the **Raw** text. They belong to you, and AI never changes them.

Recall turns speech into text with a speech model that runs on your server. The audio does not leave the machine. If you prefer a cloud model, an Admin can send the audio to a provider with an OpenAI-compatible API that accepts audio, such as OpenAI or Groq.

An Admin can also connect a **model provider**, such as Anthropic, OpenAI, or another service with an OpenAI-compatible API. Then a **Cleanup** reads your Raw text and makes a polished copy, the **Cleaned** text. A Cleanup also writes a short recap of the Session and proposes topics, moods, and the people that you mention. You accept or reject each proposal. **Corrections** teach Recall the names and the words that it hears wrong.

Recall also writes **Summaries** of a day, a week, a month, a quarter, and a year. Each Summary reads your Sessions or the Summaries below it.

[CONTEXT.md](CONTEXT.md) defines each of these words.

## What you need

- A machine that is on all the time, for example a home server, a NAS, or a small VPS.
- Docker Engine 25 or later, with Docker Compose.
- 5 GB of free disk for the build, and 2.5 GB of memory for the container.
- An OpenID Connect login provider, for example Keycloak or Authentik. Recall has no passwords of its own.
- HTTPS, for the mic on your phone. A browser turns on the mic only on HTTPS or on `localhost`. [Tailscale serve](docs/install.md#tailscale-serve) gives you HTTPS with no domain and no open port.

## Install Recall with a coding agent

A coding agent, such as Claude Code or Codex, can do the install for you. Open the agent on the machine that runs Recall. Then paste this prompt:

```text
Install Recall, a self-hosted voice journal, on this machine. The repository is
https://github.com/pdevito3/Recall. Recall is alpha software.

1. Read docs/install.md of the repository in full before you run a command. Obey it.
   It is the source of truth, and it wins over this prompt.
2. Make sure that Docker Engine 25 or later and Docker Compose are installed.
   Use `docker compose` with a space. If one of them is missing, stop and tell me.
3. Clone the repository into a folder that I choose. Ask me for the folder.
4. Ask me where to keep the data: the Docker volume (the default) or a folder of
   this machine. The data must be on a local disk. It cannot be on a network share,
   and on Unraid it cannot be under /mnt/user.
5. Ask me which OpenID Connect login provider I use, and for its issuer URL,
   client ID, and client secret. Do not print the secret back to me.
6. Ask me how I will reach Recall over HTTPS: Tailscale serve, Caddy with my own
   domain, or another proxy. Do the HTTPS steps of docs/install.md for my choice.
7. Make .env from .env.example. Fill in only the values that I gave you.
8. Tell me each callback URL to register at my login provider. Wait until I say
   that I registered it.
9. Warn me that the first person who signs in becomes the Owner. Make sure that
   open registration at my provider is off before I sign in.
10. Run `docker compose up -d`. The first start builds the image, and the build
    takes some minutes.
11. Run `docker compose exec recall /app/healthcheck.sh` until it prints Healthy.
    If it does not, read `docker compose logs recall` and the Troubleshoot table
    of docs/install.md, and correct the cause.
12. Give me the URL to open, and the backup commands of docs/install.md.

Ask me each question when you get to its step. Do not guess a value that I did
not give you.
```

## Install Recall by hand

Do these steps to start Recall on port 8080:

1. Clone the repository:

   ```bash
   git clone https://github.com/pdevito3/Recall.git
   cd Recall
   ```

2. Start the container:

   ```bash
   docker compose up -d
   ```

3. Open `http://<address of this machine>:8080`. With no login provider, the page tells you what to set.

Then [connect a login provider](docs/install.md#connect-a-login-provider) and [turn on HTTPS](docs/install.md#turn-on-https). [docs/install.md](docs/install.md) also gives the steps to keep your data, back it up, upgrade, and troubleshoot.

## Learn more

| Doc | What it tells you |
|-----|-------------------|
| [docs/install.md](docs/install.md) | The container, `/data`, HTTPS, the health probe, backup, and upgrade |
| [docs/authentication.md](docs/authentication.md) | The login, the Owner, sessions, and the login providers |
| [docs/troubleshooting.md](docs/troubleshooting.md) | The logs of an install, and how to send a support bundle |
| [CONTEXT.md](CONTEXT.md) | The words that Recall uses, such as Session, Raw, and Cleanup |
| [docs/adr/](docs/adr/) | The decisions behind the design |

## Develop Recall

Recall is a .NET Aspire application. The parts are:

- `Recall.AppHost` starts the stack.
- `Recall.Server` is the API (.NET 10, CQRS with MediatR, multi-tenant). It runs the login, keeps the sessions, and serves the SPA in production.
- `frontend` is the React 19 SPA with TanStack Router, TanStack Query, and Tailwind v4.
- `tests` holds the unit, integration, and functional tests.
- SQLite with EF Core holds the app data and the sessions in `recall.db`.

Recall started from [FullstackTemplate](https://github.com/pdevito3/FullstackTemplate) at commit `e1cffd29052419331957407033c38ec71f62800d`. [ADRs 0013 to 0016](docs/adr/) give the target shape of the code.

Read [AGENTS.md](AGENTS.md) before you change code. It gives the rules of the repository and an index of the docs for each area. The `.claude/skills/` folder holds the skills for coding agents.

### Run the stack

```bash
dotnet tool restore   # once: installs the pinned Aspire CLI as `dotnet aspire`
dotnet aspire run --apphost Recall.AppHost
```

This command starts the Aspire dashboard, the Server, the Vite dev server, and the auth provider that the `AuthProvider` setting selects. The UI starts after the Server is healthy, and the Server starts after the auth provider is healthy. Until then, the dashboard shows the UI as `Waiting`. To select the auth provider or find the test users, see [docs/authentication.md](docs/authentication.md).

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
| 24713 | `server` | https |
| 24714 | `server` | http |
| 24730 | `keycloak` | http |
| 24731 | `keycloak-postgres` | tcp |
| 24740 | `zerofactor` | http |
| 24750 | `fusionauth` | http |
| 24751 | `fusionauth-postgres` | tcp |
| 24760 | `authentik` | http |
| 24761 | `authentik-postgres` | tcp |
| 24770 | `cognito` | http |

The dashboard ports are in `Recall.AppHost/Properties/launchSettings.json`. The server ports are in `Kestrel:Endpoints` of `Recall.Server/appsettings.Development.json`. The other ports are in `Recall.AppHost/AppHost.cs` and in the class of each auth provider. A new resource takes a port in the block. A new auth provider takes the next free group of ten. `PortTableTests` fails when an endpoint of the AppHost has no row in this table or has a different port.

The provider seeds get the app URLs from the AppHost, so the redirect URIs agree with these ports. Keycloak and FusionAuth seed their database only when it is empty. If a provider database is from a run with other ports, delete its volume once (`docker volume ls`, then `docker volume rm <volume>`).

### Run several stacks at the same time

To run a stack in each git worktree, start each one in isolated mode:

```bash
dotnet aspire run --isolated --apphost Recall.AppHost
```

An isolated run gives each endpoint a random port and uses a copy of the user secrets. It also starts with empty data files in a new temp directory. A normal run uses the fixed ports of the table above and keeps its data files in `.data` of the checkout. `RECALL_DATA_DIR` sets another directory.

## License

MIT
