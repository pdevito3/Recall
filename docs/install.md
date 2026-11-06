# Install Recall

Recall runs as one container with one volume. All data is in `/data` in the container. You need Docker with Docker Compose.

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

   If Compose cannot pull the image, for example before the first release, it prints an error and builds the image from this checkout. Keep 5 GB of disk free for the build, and use Docker Engine 25 or later.

3. Open `http://<address of this machine>:8080`. With no login provider, the page says what to set and gives the callback URL.

To use another port, add `RECALL_PORT=8090` to `.env`. See [Connect a login provider](#connect-a-login-provider) for `.env`.

## Keep your data

`/data` holds the database `recall.db`, the stored files in `files/`, and the keys in `keys/`. Later releases add `logs/` and `models/` to the same folder.

`compose.yaml` keeps `/data` in the Docker volume `recall_recall-data`. `docker compose down` keeps the volume. `docker compose down -v` deletes it, and all of your data with it.

**A run with no `/data` mount loses data.** If you start the image with `docker run` and no `-v` for `/data`, Docker makes an anonymous volume for that one container. A new container does not get that volume, so an upgrade starts with an empty journal. `docker run --rm` deletes the volume when the container stops.

`/data` must be on a local disk of the machine. The Server refuses to start on a network file system, on a folder that a hypervisor or Docker Desktop shares from the host, and on a FUSE file system other than `fuseblk`, because SQLite WAL is safe only on a local disk.

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

A new folder is an empty journal, and the first person who signs in becomes its Owner. To keep the data of the Docker volume, copy it before step 3. Use your own folder and IDs in this command:

```bash
docker compose stop
docker run --rm -v recall_recall-data:/data:ro -v /volume1/docker/recall:/new alpine sh -c 'cp -a /data/. /new/ && chown -R 1026:100 /new'
```

After you see your journal in the folder, remove the old volume with `docker volume rm recall_recall-data`. Then no command can use the old copy by mistake.

Do not set `PUID` or `PGID` with the Docker volume. The volume belongs to user `1654`, so another user cannot write to it. The Server then cannot start, and the log says `The data directory /data is not writable`.

## Connect a login provider

Recall has no passwords. A person signs in with your OpenID Connect provider, for example Keycloak.

1. At the provider, make a confidential client that uses the authorization code flow. In Keycloak, turn on **Client authentication** and **Standard flow**.
2. Add the callback URL as a redirect URI of the client. The URL is `http://<address of this machine>:8080/auth/oidc/callback`, and the login page of Recall shows it. Do not use a wildcard.
3. Make the file `.env`, and edit the values. If `.env` exists, this command keeps it, so copy the lines of the provider from `.env.example` into it.

   ```bash
   cp -n .env.example .env
   ```

   `Auth__Providers__0__Issuer` is the issuer URL. In Keycloak, it is `https://<keycloak address>/realms/<realm>`. The browser and the container must both reach the provider at this URL. So do not use `localhost`, because in the container `localhost` is the container. For an issuer on `http`, remove the `#` in front of `Auth__Providers__0__AllowHttp=true`. Put a value that has a `$` or a `#` in single quotes.

4. Run `docker compose up -d`. Compose makes a new container with the new values.
5. **Prepare the first login.** The first person who signs in becomes the Owner, an Admin of the journal. If your provider has open registration, a stranger who registers and signs in first owns your journal. Before the first login, turn off open registration, or limit the client to your own account.
6. Open Recall, and sign in.

After the first login, Recall refuses each other person until you set `Auth__OpenSignup=true`. To recover a lost Owner, see [docs/authentication.md](authentication.md#recover-a-lost-owner). The other settings are in [Login providers](authentication.md#login-providers).

`compose.yaml` passes only the names in its `environment` list. To pass another setting, make the file `compose.override.yaml`. Do not edit `compose.yaml`, because a later `git pull` can then stop. For example, this file gives a private CA to the Server, and user `1654` must be able to read `ca.pem`:

```yaml
services:
  recall:
    environment:
      - Auth__Providers__0__CaCertificatePath=/certs/ca.pem
    volumes:
      - ./ca.pem:/certs/ca.pem:ro
```

Compose reads this file when you give no `-f` option. With `compose.tls.yaml`, also give `-f compose.override.yaml`.

## Turn on HTTPS

A browser turns on the mic only on HTTPS or on `localhost`. On plain HTTP at a LAN address, Recall says that the mic stays off. Use one of these options.

Each address of Recall needs its own callback URL at the provider. `/auth/status` gives the URL of the address that you open.

### Tailscale serve

This option needs no open port and no domain of your own. Tailscale gets a real certificate for the machine, and the devices of your tailnet can reach Recall through it.

1. Install Tailscale on the machine, and turn on HTTPS for your tailnet in the Tailscale admin console.
2. Run this command on the machine:

   ```bash
   tailscale serve --bg 8080
   ```

   If you set `RECALL_PORT`, use that port.

3. Add `https://<machine>.<tailnet>.ts.net/auth/oidc/callback` as a redirect URI at the provider.
4. Open `https://<machine>.<tailnet>.ts.net`.

Port 8080 stays open on your LAN. To close it, add `RECALL_PORT=127.0.0.1:8080` to `.env`, with your own port if you use another port, and run `docker compose up -d`. Tailscale serve still reaches Recall.

### Caddy

`compose.tls.yaml` adds a Caddy container in front of Recall, and it closes the plain HTTP port of Recall. This option needs a domain name that points to the machine.

1. Add the address to `.env`:

   ```bash
   RECALL_TLS_ORIGIN=https://recall.example.com
   ```

2. Add `https://recall.example.com/auth/oidc/callback` as a redirect URI at the provider.
3. Start both containers:

   ```bash
   docker compose -f compose.yaml -f compose.tls.yaml up -d
   ```

Give both `-f` options in each later `docker compose` command. If Compose stops with an error about the `!reset` tag in `compose.tls.yaml`, update Docker Compose.

If another app has port 443, put the port in the address and set `RECALL_TLS_PORT` to the same port. For example, `RECALL_TLS_ORIGIN=https://recall.example.com:8443` and `RECALL_TLS_PORT=8443`. If another app has port 80, set `RECALL_TLS_HTTP_PORT`. Caddy proves the domain through port 80 or port 443 of the internet, so keep one of these two ports open to Caddy.

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

It prints the answer of `/health`. `Healthy` passes. `Degraded` means that the file store cannot take a write, and it fails. `Unhealthy` and `No answer from /health` also fail. A sleeping disk can give a slow answer, so the probe asks up to four times, 10 seconds apart, before it fails. `docker compose ps` shows the result of the last probe. Docker runs the probe each 5 seconds during the first 2 minutes, until a probe passes. Docker Engine 25 and later do this. An older engine shows `starting` until the first hour ends.

A probe writes to `/data/files` and flushes it to the disk, at most one time in 30 seconds. In a test of 180 seconds on an idle server, `/data/files` changed at each probe with a probe each 30 seconds, and no path in `/data` changed with no probe. So a short interval keeps a NAS disk awake. A probe each hour lets a disk with a shorter sleep timer sleep between two probes, and the probe wakes it one time each hour. A cleanup of old logins can also write to the database one time each hour. To change this, set `RECALL_PROBE_INTERVAL` in `.env`, for example `RECALL_PROBE_INTERVAL=6h`.

## Back up and restore

A backup is a copy of `/data` while Recall is stopped. The copy holds the journal and the keys, so keep it private. These commands use the Docker volume. They write `recall-backup.tar.gz` in the current folder, and only you can read that file. If the commands do not print `The backup is complete`, the backup failed. The file of the last backup then stays, and the failed backup stays as `recall-backup.new.tar.gz`.

To back up:

```bash
docker compose stop
(umask 077; docker run --rm -v recall_recall-data:/data:ro alpine tar -C /data -czf - . > recall-backup.new.tar.gz) &&
  tar -tzf recall-backup.new.tar.gz > recall-backup.list &&
  grep -qx './recall.db' recall-backup.list &&
  mv recall-backup.new.tar.gz recall-backup.tar.gz &&
  echo 'The backup is complete'
docker compose start
```

To restore on a new container, first examine the file. This command must print `The backup has a database`. Do not continue if it does not, because the next commands delete the volume.

```bash
tar -tzf recall-backup.tar.gz > recall-backup.list && grep -qx './recall.db' recall-backup.list && echo 'The backup has a database'
```

Then run these commands:

```bash
docker compose down
docker volume rm recall_recall-data
docker compose up --no-start
docker run --rm -i -v recall_recall-data:/data alpine tar -C /data -xzpf - < recall-backup.tar.gz
docker compose up -d
```

With a folder in place of the volume, stop Recall and copy the folder with `sudo cp -a`, which keeps the owners.

## Upgrade

1. Make a backup. An upgrade can change the database, and an older image is not made for the changed database. To go back to an older version, restore the backup.
2. Get the new version, and start it:

   ```bash
   git pull
   docker compose up -d --build
   ```

The Server runs its migrations at startup, and the volume stays.

After the first release, you can use the published image in place of a build. Run `docker compose pull` and `docker compose up -d`. To stay on one version, set `RECALL_TAG` in `.env` to that version.

## Troubleshoot

| What you see | Cause | What to do |
|---|---|---|
| `docker compose ps` shows `Restarting`, and `docker compose logs` says `is on the mount` | `/data` is on a file system that the Server refuses. See [Keep your data](#keep-your-data) | Move `/data` to a local disk |
| `docker compose ps` shows `Restarting`, and the log says `is not writable` | The user of the container cannot write to `/data` | With a folder, set `PUID` and `PGID` to its owner. With the Docker volume, remove `PUID` and `PGID` |
| `docker compose ps` shows `Restarting`, and the log names `Auth:Providers:0` | A setting of the provider is missing or wrong | Correct the value in `.env`, and run `docker compose up -d` |
| The provider says that the redirect URI is not valid | The callback URL of this address is not registered, or a proxy changed `Host` | Register the URL from `/auth/status`, and see [Another proxy](#another-proxy) |
| The page says `This server does not accept new people` | The journal has an Owner, and signup is closed | See [First login and the Owner](authentication.md#first-login-and-the-owner) |
