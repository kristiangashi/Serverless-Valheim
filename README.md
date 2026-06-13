# Serverless Valheim 🛡️

Self-host one shared Valheim world with friends — no paid dedicated server, no 30-day
save wipes. Whoever's free hosts the **latest** copy of the world; everyone else joins them.

This isn't a game server. It's a small **coordination layer** that guarantees exactly one
person holds the latest world at a time, and tells everyone else the join code.

## How it works

Valheim doesn't need a server for compute — any player can host a session. The only hard
part is keeping a single, shared, up-to-date world file. This solves that with a
distributed lock + versioned file sync:

1. The world archive lives on the coordinator (canonical copy — no one's local copy is authoritative).
2. To host, you **claim** the world. That's an atomic, single-writer lock — only one person can hold it.
3. You download the latest world, drop it into your Valheim folder, host the game, and share the join code.
4. Autosave uploads run during play; **Upload & finish** saves and frees the world for the next host.
5. If a host crashes, the lease expires (default 5 min) and the world frees automatically.

### Safety: how it avoids corrupting/forking the world

- **Single-writer lock** — you can't host without holding it; acquire is atomic server-side.
- **Monotonic version** — every upload bumps a version; the server rejects an upload whose
  `baseVersion` doesn't match the current version, so a stale client can't clobber newer data.
- **Lease + heartbeat** — the host's browser tab pings every 30s; an expired lease frees the lock.
- **Download-before-host** — always pull the latest before hosting; never host off a stale copy.

## Project status

- **Phase 0 (this repo): the coordinator + manual web UI.** Friends drag the world zip in/out
  of their save folder themselves. Proves the lock/version protocol.
- **Phase 1 (next): a local helper `.exe`** that auto-syncs files to the Valheim folder so
  there's no manual zipping. This is the real end product.
- **Phase 2: polish** — auto-detect the Valheim folder, auto-launch, "world is free" notifications.

## Architecture

- **Coordinator API** — ASP.NET minimal API (`src/Coordinator`). Holds lock state, version,
  host, join code. State persists to `data/state.json` so a restart keeps the lock.
- **Blob storage** — `IBlobStorage` abstraction. Phase 0 uses local disk (`LocalDiskBlobStorage`).
  **Production should use Cloudflare R2** (free 10 GB, no egress) because Railway's disk is ephemeral.
- **Web UI** — single static page in `wwwroot/index.html`.

## Run locally

```bash
cd src/Coordinator
dotnet run
```

Then open the printed `http://localhost:<port>`. Default group passphrase is `valheim`.

## Configuration (environment variables)

| Var | Default | Purpose |
|---|---|---|
| `PORT` | (Kestrel default) | Port to bind. Railway sets this automatically. |
| `GROUP_PASSPHRASE` | `valheim` | Shared passphrase friends need to claim/download. **Change this.** |
| `ADMIN_PASSPHRASE` | `changeme-admin` | For force-releasing a stuck lock. **Change this.** |
| `DATA_DIR` | `data` | Where state + world archives are stored. |
| `LEASE_MINUTES` | `5` | How long a lock survives without a heartbeat. |
| `KEEP_VERSIONS` | `3` | How many old world versions to retain. |
| `R2_ACCOUNT_ID` | — | Cloudflare account ID. Set all four `R2_*` to use R2. |
| `R2_ACCESS_KEY_ID` | — | R2 API token Access Key ID. |
| `R2_SECRET_ACCESS_KEY` | — | R2 API token Secret Access Key. |
| `R2_BUCKET` | — | R2 bucket name. |

When all four `R2_*` vars are set, world archives are stored in **Cloudflare R2** (durable).
Otherwise the coordinator falls back to local disk. The startup log prints which store is active.

## Deploy to Railway

The included `Dockerfile` builds and runs the coordinator; Railway injects `$PORT` automatically.
Set `GROUP_PASSPHRASE` and `ADMIN_PASSPHRASE` as Railway variables before sharing the URL.

> ⚠️ Railway's container disk is **ephemeral** — a redeploy wipes `data/`. Before friends rely
> on it for a real world, wire up Cloudflare R2 (a `R2BlobStorage : IBlobStorage`) or attach a
> Railway volume mounted at `DATA_DIR`.

## API reference

| Method | Path | Body | Purpose |
|---|---|---|---|
| `GET` | `/api/state` | — | Public state (no token). |
| `POST` | `/api/claim` | `{displayName, passphrase}` | Acquire the lock → `{token, version}`. |
| `POST` | `/api/heartbeat` | `{token}` | Renew the lease. |
| `POST` | `/api/joincode` | `{token, joinCode}` | Share the Valheim join code. |
| `POST` | `/api/upload` | multipart: `token, finish, baseVersion, file` | Upload world, bump version, optionally release. |
| `GET` | `/api/download?passphrase=` | — | Download the latest world archive. |
| `POST` | `/api/release` | `{token}` | Release the lock (no save). |
| `POST` | `/api/admin/force-release` | `{adminPassphrase}` | Break a stuck lock. |
