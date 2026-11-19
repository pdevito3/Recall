# Recall

> [!WARNING]
> **Recall is in alpha development. Do not use it in production.** The data model, the API, and the install steps change from one commit to the next, and no release exists yet. An upgrade can need a manual step, and it can lose data. Keep a backup of anything that you write in Recall.

Recall is a self-hosted journal for the voice. You open Recall on your phone, talk, and your words become a journal entry. You run Recall on your own machine. The audio and the text stay on your own disk, unless an Admin connects a cloud model provider.

## What Recall does

Each time that you journal, Recall makes a **Session**. You can dictate or type into it. Recall stores your audio before it starts to transcribe, so a lost connection or a crash does not lose your words. The words that you said are the **Raw** text. They belong to you, and AI never changes them.

Recall turns speech into text with a speech model that runs on your server. The audio does not leave the machine. If you prefer a cloud model, an Admin can send the audio to a provider with an OpenAI-compatible API that accepts audio, such as OpenAI or Groq.

An Admin can also connect a **model provider**, such as Anthropic, OpenAI, or another service with an OpenAI-compatible API. Then a **Cleanup** reads your Raw text and makes a polished copy, the **Cleaned** text. A Cleanup also writes a short recap of the Session and proposes topics, moods, and the people that you mention. You accept or reject each proposal. **Corrections** teach Recall the names and the words that it hears wrong.

Recall also writes **Summaries** of a day, a week, a month, a quarter, and a year. Each Summary reads your Sessions or the Summaries below it.

[CONTEXT.md](CONTEXT.md) defines each of these words.

## What you need

- A machine that is on all the time, for example a home server, a NAS, or a small VPS.
- Docker Engine 25 or later, with Docker Compose.
- 6 GB of free disk for the build and the speech model, and more for your audio.
- 3 GB of memory for the container.
- An OpenID Connect login provider, for example Keycloak or Authentik. Recall has no passwords of its own.
- HTTPS, for the mic on your phone. A browser turns on the mic only on HTTPS or on `localhost`. [Tailscale serve](docs/install.md#tailscale-serve) gives you HTTPS with no domain and no open port.

## Install Recall with a coding agent

A coding agent, such as Claude Code or Codex, can do the install for you. Open the agent on the machine that runs Recall. Then paste this prompt:

```text
Install Recall, a self-hosted voice journal, on this machine. The repository is
https://github.com/pdevito3/Recall. Recall is alpha software.

Safety rules for the whole install:
- Never run `docker compose down -v` or `docker volume rm`, and never delete or
  chown a data folder, unless I say yes to that exact command.
- Do not edit compose.yaml. If a setting needs it, use compose.override.yaml.
- Never ask me for a secret, and never print one.
- Ask me each question when you get to its step. Do not guess a value.

1. Make sure that git, Docker Engine 25 or later, and Docker Compose are
   installed. Use `docker compose` with a space. If one is missing, stop and
   tell me.
2. If `docker compose ls -a` shows a `recall` project, `docker ps -a --filter
   label=com.docker.compose.project=recall` shows a container, or `docker volume
   ls` shows `recall_recall-data`, stop and tell me. Recall is already on this
   machine.
3. Ask me for a folder, and clone the repository into it. If the clone fails
   for access, stop. Tell me to run `gh auth login` and to accept the Git
   credential setup, or to give you access to `gh repo clone pdevito3/Recall`.
4. Read docs/install.md of the checkout in full. Obey it. It is the source of
   truth, and it wins over the numbered steps of this prompt. It never wins
   over the safety rules.
5. Run `cp -n .env.example .env` in the checkout.
6. Ask me where to keep the data: the Docker volume (the default) or a folder of
   this machine. The data must be on a local disk, not on a network share. On
   Unraid, it cannot be under /mnt/user. On a Mac, use the Docker volume.
7. Ask me which OpenID Connect login provider I use, its issuer URL, and its
   client ID. Do not ask for the client secret.
8. Ask me how I will reach Recall over HTTPS: Tailscale serve, Caddy with my own
   domain, or another proxy. Do the HTTPS steps of docs/install.md for my choice.
   With a public domain, also set AllowedHosts (docs/install.md#set-allowedhosts).
9. Write to .env each value that docs/install.md names for my answers to steps 6
   to 8. Remove the `#` of each `Auth__Providers__0__` line, except `AllowHttp`.
   Remove the `#` of `AllowHttp` only for an issuer on http. Set `DisplayName`
   to the name of my provider, and `Id` to a lowercase form of that name.
10. Leave `Auth__Providers__0__ClientSecret=` empty. Tell me to open .env in an
    editor and type the secret there, in single quotes if it has a `$` or a `#`.
    Wait until I say that I did it. Then make sure that the line is not empty,
    with no output of its value, and run `chmod 600 .env`.
11. Tell me each URI to register at my provider, for each address that I will
    use: the redirect URI `/auth/oidc/callback`, and the post-logout redirect URI
    `/auth/signed-out` unless my provider is Authentik. No wildcards. See
    docs/authentication.md#register-the-callback-urls. Wait until I say that I
    registered them.
12. Tell me that the first person who signs in becomes the Owner. Ask me to turn
    off open registration at my provider, or to limit the client to my account.
    Do not continue until I say that I did it.
13. Run `docker compose up -d`. The first start builds the image, and the build
    takes some minutes.
14. Run `docker compose exec recall /app/healthcheck.sh` until it prints Healthy,
    for up to 10 minutes. If it does not, read `docker compose logs recall` and
    the Troubleshoot table of docs/install.md. Tell me the cause and your fix
    before you change anything.
15. Healthy does not test the login provider. So fetch `/auth/status` at the
    HTTPS URL. Its `state` must be `ready`, and no login method can be
    `unreachable`. Its callback URL must be one that I registered in step 11.
16. Give me the URL to open, and the backup commands of docs/install.md.
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
| [docs/backend.md](docs/backend.md) | The structure of the Server, and how to add a feature |
| [docs/frontend.md](docs/frontend.md) | The structure of the SPA, the API client, and the routes |
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

Read [AGENTS.md](AGENTS.md) before you change code. It gives the rules of the repository and an index of the docs for each area. The `.agents/skills/` folder holds the skills for coding agents.

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
