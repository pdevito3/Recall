# Install Recall

Recall runs as one container with one volume. All data is in `/data` in the container. These steps were tested with Docker Compose 5.3.1.

## Start Recall

1. Get the files:

   ```bash
   git clone https://github.com/pdevito3/Recall.git
   cd Recall
   ```

2. Start the container:

   ```bash
   docker compose up -d
   ```

   If no image is published, Compose builds the image from this checkout. The build needs about 5 GB of free disk and some minutes.

3. Open `http://<address of this machine>:8080`. With no login provider, the page says what to set and gives the callback URL.

To use another port, add `RECALL_PORT=8090` to `.env`. See [Connect a login provider](#connect-a-login-provider) for `.env`.

## Keep your data

`/data` holds the database `recall.db`, the stored files in `files/`, and the keys in `keys/`. Later releases add `logs/` and `models/` to the same folder.

`compose.yaml` keeps `/data` in the Docker volume `recall_recall-data`. `docker compose down` keeps the volume. `docker compose down -v` deletes it, and all of your data with it.

**A run with no `/data` mount loses data.** If you start the image with `docker run` and no `-v` for `/data`, Docker makes an anonymous volume for that one container. A new container does not get that volume, so an upgrade starts with an empty journal. `docker run --rm` deletes the volume when the container stops.

`/data` must be on a local disk of the machine. The Server refuses to start on a network, FUSE, or shared folder file system, because SQLite WAL is safe only on a local disk.

**On Unraid, the path must not be under `/mnt/user`.** It is a FUSE file system. Use a path such as `/mnt/cache/appdata/recall` or `/mnt/disk1/appdata/recall`.

On a Mac, keep the Docker volume. Docker Desktop shows a folder of the Mac as the shared folder type `fakeowner`, so the Server refuses to start on it.

### Use a folder in place of the Docker volume

The container runs as user `1654` and group `1654`, not as root. A new Docker volume gets the correct owner from the image. A folder of the machine keeps its own owner, so you must tell the container who owns the folder.

1. Make the folder. Give the IDs of the user and the group that own it. For example, for user `1026` and group `100`:

   ```bash
   sudo install -d -o 1026 -g 100 /volume1/docker/recall
   ```

2. Add these lines to `.env`:

   ```bash
   RECALL_DATA=/volume1/docker/recall
   PUID=1026
   PGID=100
   ```

3. Run `docker compose up -d`.

Do not set `PUID` or `PGID` with the Docker volume. The volume belongs to user `1654`, so another user cannot write to it, and the Server stops with `The data directory /data is not writable`.

## Connect a login provider

Recall has no passwords. A person signs in with your OpenID Connect provider, for example Keycloak.

1. At the provider, make a confidential client that uses the authorization code flow. In Keycloak, turn on **Client authentication** and **Standard flow**.
2. Add the callback URL as a redirect URI of the client. The URL is `http://<address of this machine>:8080/auth/oidc/callback`, and the login page of Recall shows it. Do not use a wildcard.
3. Make the file `.env`, and edit the values:

   ```bash
   cp .env.example .env
   ```

   `Auth__Providers__0__Issuer` is the issuer URL. In Keycloak, it is `https://<keycloak address>/realms/<realm>`. The browser and the container must both reach the provider at this URL. So do not use `localhost`, because in the container `localhost` is the container. For an issuer on `http`, remove the `#` in front of `Auth__Providers__0__AllowHttp=true`.

4. Run `docker compose up -d`. Compose makes a new container with the new values.
5. **Prepare the first login.** The first person who signs in becomes the Owner, an Admin of the journal. If your provider has open registration, a stranger who registers and signs in first owns your journal. Before the first login, turn off open registration, or limit the client to your own account.
6. Open Recall, and sign in.

After the first login, Recall refuses each other person until you set `Auth__OpenSignup=true`. To recover a lost Owner, see [docs/authentication.md](authentication.md#recover-a-lost-owner). A private CA and the other settings are in [Login providers](authentication.md#login-providers). `compose.yaml` passes only the names in its `environment` list, so add a line there for each other setting that you use.

## Turn on HTTPS

A browser turns on the mic only on HTTPS or on `localhost`. On plain HTTP at a LAN address, Recall says that the mic stays off. Use one of these options.

Each address of Recall needs its own callback URL at the provider. `/auth/status` gives the URL of the address that you open.

### Tailscale serve

This option needs no open port and no domain. Each device of your tailnet gets a real certificate.

1. Install Tailscale on the machine, and turn on HTTPS for your tailnet in the Tailscale admin console.
2. Run this command on the machine:

   ```bash
   tailscale serve --bg 8080
   ```

3. Add `https://<machine>.<tailnet>.ts.net/auth/oidc/callback` as a redirect URI at the provider.
4. Open `https://<machine>.<tailnet>.ts.net`.

### Caddy

`compose.tls.yaml` adds a Caddy container in front of Recall, and it closes the plain HTTP port of Recall. This option needs a domain name that points to the machine. Ports 80 and 443 must be open from the internet, so that Caddy can get a certificate.

1. Add the address to `.env`:

   ```bash
   RECALL_TLS_ORIGIN=https://recall.example.com
   ```

2. Add `https://recall.example.com/auth/oidc/callback` as a redirect URI at the provider.
3. Start both containers:

   ```bash
   docker compose -f compose.yaml -f compose.tls.yaml up -d
   ```

Give both `-f` options in each later `docker compose` command.

If another app has port 443, put the port in the address and set `RECALL_TLS_PORT` to the same port. For example, `RECALL_TLS_ORIGIN=https://recall.example.com:8443` and `RECALL_TLS_PORT=8443`. If another app has port 80, set `RECALL_TLS_HTTP_PORT`. A public certificate then needs another way to prove the domain, which is not part of this file.

### Another proxy

A proxy must keep the original `Host` header and set `X-Forwarded-Proto`. Caddy and Tailscale serve do this. See [Keep Host in a proxy](authentication.md#keep-host-in-a-proxy) for nginx.

## Set AllowedHosts

Set `AllowedHosts` when a person that you do not trust can reach Recall, for example through a public domain. Give each name of the server, with `;` between two names and no port:

```bash
AllowedHosts=recall.example.com;192.168.1.10
```

A request with another `Host` header then gets a 400. This stops a forged `Host` from changing the callback URL. See [Register the callback URLs](authentication.md#register-the-callback-urls).

## Limit logins behind a proxy

The Server limits login starts for each connection address. Behind a proxy, each connection comes from the proxy, so all clients share one bucket, and one flood blocks each new login. A session that exists keeps working. To limit each client, set the limit at the proxy.

## The health probe

Docker runs `/app/healthcheck.sh` each hour. Run it yourself to see the state now:

```bash
docker compose exec recall /app/healthcheck.sh
```

It prints `Healthy`, or it prints `Degraded` and fails when the file store cannot take a write. `docker compose ps` shows the result of the last probe. Docker runs the probe each 5 seconds during the first 2 minutes, until a probe passes. Docker Engine 25 and later do this. An older engine shows `starting` until the first hour ends.

Each probe writes to `/data/files` and flushes it to the disk. A probe each 30 seconds changes that folder at each probe, and with no probe no path in `/data` changes. So a short interval keeps a NAS disk awake. A probe each hour lets a disk with a shorter sleep timer sleep between two probes, and the probe wakes it one time each hour. To change this, set `RECALL_PROBE_INTERVAL` in `.env`, for example `RECALL_PROBE_INTERVAL=6h`.

## Back up and restore

A backup is a copy of `/data` while Recall is stopped. The copy holds the journal and the keys, so keep it private. These commands use the Docker volume, and they write the file in the current folder.

To back up:

```bash
docker compose stop
docker run --rm -v recall_recall-data:/data:ro -v "$PWD":/backup alpine tar -C /data -czf /backup/recall-backup.tar.gz .
docker compose start
```

To restore on a new container:

```bash
docker compose down -v
docker compose up --no-start
docker run --rm -v recall_recall-data:/data -v "$PWD":/backup alpine tar -C /data -xzpf /backup/recall-backup.tar.gz
docker compose up -d
```

With a folder in place of the volume, stop Recall and copy the folder with `cp -a`.

## Upgrade

1. Make a backup. An upgrade can change the database, and an older image cannot read the changed database.
2. Get the new version:

   ```bash
   git pull
   docker compose pull
   docker compose up -d --build
   ```

The Server runs its migrations at startup, and the volume stays.

To stay on one version, set `RECALL_TAG` in `.env` to that version.

## Troubleshoot

| What you see | Cause | What to do |
|---|---|---|
| The container stops, and `docker compose logs` says `is on the mount` | `/data` is on a network, FUSE, or shared folder file system | Move `/data` to a local disk |
| The container stops, and the log says `is not writable` | The user of the container cannot write to `/data` | Set `PUID` and `PGID` to the owner of the folder |
| The container stops, and the log names an `Auth__Providers` setting | A setting of the provider is missing or wrong | Correct the value in `.env`, and run `docker compose up -d` |
| The provider says that the redirect URI is not valid | The callback URL of this address is not registered, or a proxy changed `Host` | Register the URL from `/auth/status`, and see [Another proxy](#another-proxy) |
| The page says `This server does not accept new people` | The journal has an Owner, and signup is closed | See [First login and the Owner](authentication.md#first-login-and-the-owner) |
