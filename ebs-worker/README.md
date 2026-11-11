# ebs-worker — the Twitch Extension Backend Service on Cloudflare Workers

A Cloudflare Worker (Hono + TypeScript + D1) that does what [`src/EDNexus.Ebs`](../src/EDNexus.Ebs/README.md)
does: it bridges the EDNexus desktop app and the Twitch extension frontend in [`extension/`](../extension/README.md).
The desktop app POSTs state here; the Worker verifies the caller and relays the update to viewers through
Twitch's Extensions PubSub API. It is also the desktop app's only path to Twitch login (the real OAuth 2.0
Authorization Code handshake with PKCE, issuing its own opaque token back to the desktop).

**The HTTP contract is unchanged**, so the desktop app and the extension need no change: same routes, status codes,
headers (`Retry-After`, `X-EDNexus-Pubsub-Rejected`, `X-EDNexus-Snapshot-Removed`), problem+json bodies, limits and
CORS policy. The API is documented in the [.NET README](../src/EDNexus.Ebs/README.md#api); the sections there on
*OAuth login flow*, *API*, *Rate limits* and *Privacy* apply as written. This README covers what is different.

## What changed from the ASP.NET service

| ASP.NET service | Worker |
|---|---|
| SQLite file `ebs.db` on a volume | D1 database `ednexus-ebs` (`migrations/0001_init.sql`) |
| Data Protection key ring on a separate volume | AES-GCM with the `TOKEN_ENCRYPTION_KEY` secret (also kept apart from the database) |
| Pending OAuth sessions and one-time codes in process memory | The `oauth_pending` table (short-lived; code payloads are encrypted, since they carry Twitch tokens) |
| Three `BackgroundService`s | Cron Triggers: prune (15 min), token refresh (30 min), credential check (6 h) |
| Per-channel `ChannelLocks` | None (see [Concurrency](#concurrency)) |
| ASP.NET rate limiter, `X-Forwarded-For` from a trusted proxy | Rate Limiting bindings for per-IP limits, a D1 counter for per-channel limits; client address is `CF-Connecting-IP` |
| Refuses to start on bad configuration | Answers `503 {"status":"misconfigured"}` and logs every problem (`wrangler tail`) |
| Docker image + Caddy | `wrangler deploy` |

Sessions or broadcaster tokens in the old database are **not** migrated (they are encrypted with a Data Protection
key ring that does not exist here). Every broadcaster signs in again once; the desktop app prompts for it on the
first `401`. Published cards reappear within the desktop's 10-minute heartbeat.

## Setup

```sh
cd ebs-worker
npm install
cp wrangler.example.jsonc wrangler.jsonc        # gitignored: it holds YOUR D1 database id
npx wrangler d1 create ednexus-ebs              # paste the database_id it prints into wrangler.jsonc
npm run db:migrate:remote
```

The repository is public, so nothing that identifies a Cloudflare account is committed: the D1 id lives only in your
local `wrangler.jsonc` and in the `CLOUDFLARE_D1_DATABASE_ID` GitHub secret.

### Secrets

Set each with `npx wrangler secret put NAME` (a masked prompt; never pass one as an argument):

| Secret | What it is |
|---|---|
| `TWITCH_CLIENT_ID` | The Twitch application that performs the OAuth login. |
| `TWITCH_CLIENT_SECRET` | Its client secret. |
| `TWITCH_EXTENSION_ID` | The extension's Client ID, sent as the `Client-Id` of the PubSub call. Often the same value as `TWITCH_CLIENT_ID`. |
| `TWITCH_EXTENSION_SECRET` | The extension's base64 secret from the Developer Console (≥ 16 bytes decoded; Twitch issues 32). Signs the PubSub JWT. |
| `TOKEN_ENCRYPTION_KEY` | **New.** base64 of 32 random bytes: `node -e "console.log(require('crypto').randomBytes(32).toString('base64'))"`. Encrypts the Twitch tokens in D1. Losing or changing it logs every broadcaster out (they sign in again); the Worker keeps running. |

`npx wrangler secret list` prints names only, which makes it safe to audit against `src/env.ts`.

### Variables

In `wrangler.jsonc` under `vars` (all optional apart from the redirect URI, which has a default):

| Variable | Default | |
|---|---|---|
| `TWITCH_OAUTH_REDIRECT_URI` | `https://ednexus.signal-and-thread.com/oauth/callback` | Exactly as registered on the Twitch application. `http` is accepted for `localhost` only. |
| `TWITCH_OAUTH_SCOPES` | `user:read:email` | Space- or comma-separated; duplicates are dropped. |
| `TWITCH_OWNER_USER_ID` | unset | Numeric user id of the extension's owner, the `user_id` claim of the PubSub JWT. Set it if publishing fails with 401/403. |
| `MAX_STATE_PAYLOAD_BYTES` | `5000` | Compact state size forwarded to PubSub. Above `5120` (Twitch's hard limit) is refused. |
| `MAX_REQUEST_BODY_BYTES` | `16384` | Largest body `/api/update-state` and `/oauth/*` read; larger gets `413`. |
| `UPDATE_STATE_RATE_LIMIT` / `UPDATE_STATE_RATE_LIMIT_WINDOW_SECONDS` | `1` / `2` | Per authenticated channel. |
| `INITIAL_STATE_CACHE_SECONDS` | `5` | `max-age` of the public `Cache-Control` on a served snapshot; `0` disables it. |
| `OAUTH_SESSION_TTL_MINUTES` / `OAUTH_CODE_TTL_SECONDS` | `10` / `60` | |
| `TWITCH_TOKEN_REFRESH_BUFFER_MINUTES` | `60` | How far ahead of expiry a grant is refreshed. |
| `CHANNEL_STATE_MAX_AGE_HOURS` | `24` | Oldest snapshot served; `0` disables it, `1`–`11` is refused. See the [privacy note](../src/EDNexus.Ebs/README.md#privacy-the-24-hour-snapshot-window). |
| `ADDITIONAL_ALLOWED_FRONTEND_ORIGINS` | unset | Extra origins that may read `initial-state` from a browser (Developer Rig). **Leave unset in production.** |

The per-IP limits (`GET /api/initial-state`: 60 per 10 s; `/oauth/*`: 30 per 60 s, shared) are set on the
`ratelimits` bindings in `wrangler.jsonc`, not by variables: the Rate Limiting binding supports a 10 s or 60 s period
only, which is what both limits use.

### Local development

```sh
cp .dev.vars.example .dev.vars     # fill in test values; git-ignored; restart `wrangler dev` after editing it
npm run db:migrate:local
npm run dev                        # http://localhost:8787
npm test                           # vitest, no Cloudflare account needed
npm run typecheck
```

The tests run the Worker's real SQL against SQLite (`node:sqlite`) loaded with the real migration, with Twitch
stubbed at `fetch`. They cover the OAuth flow, authentication, validation, limits, the credential check, token
refresh and the cron jobs.

## Deploying

CI (`.github/workflows/ebs-worker.yml`) tests every change to `ebs-worker/`, and on `main` applies migrations then
deploys, **only when** the repository has the secrets `CLOUDFLARE_API_TOKEN` (Account → Workers Scripts: Edit and
D1: Edit), `CLOUDFLARE_ACCOUNT_ID` and `CLOUDFLARE_D1_DATABASE_ID`. Without them it tests and skips the deploy. Or by
hand: `npm run db:migrate:remote && npm run deploy`.

### Cutover from the container

1. Create the D1 database, apply the migration, set the five secrets, deploy.
2. `curl https://<workers.dev host>/healthz` → `{"status":"ok","pubsub":"unknown",...}`; an unsigned
   `POST /api/update-state` → `401`; `GET /api/initial-state/1` → `404`.
3. Attach the custom domain in the dashboard (Worker → Settings → Domains & Routes → Add → Custom Domain) once the
   container no longer owns the hostname. The registered OAuth redirect URI already points at it, so nothing changes
   on the Twitch side; if you test on `*.workers.dev` first, that redirect URI must be registered too.
4. Run the [publish smoke test](../src/EDNexus.Ebs/README.md#publish-smoke-test) from the desktop app, and watch
   `npx wrangler tail`. Within 6 hours (or run the cron by hand) `/healthz` reports `pubsub: accepted`.
5. Only then stop the container and remove `src/EDNexus.Ebs`, its tests, `ebs-docker.yml` and the Docker files.

## Behaviour notes

### Concurrency

A Worker has no process to hold a per-channel lock, so a publish and a sign-out can interleave. The invariant
(*no snapshot outlives its credential*) is kept by ordering instead: a publish re-checks its token before storing and
again afterwards, and takes the card back down if the token has gone; a sign-out clears, deletes the token, then
removes the snapshot once more. Every interleaving of the two ends with no snapshot and no live credential. The
residual cost is that viewers watching during that sub-second window can see the card flash on and then be sent
`offline` again. If that ever matters, a per-channel Durable Object is the way to get real serialisation.

### Limits worth knowing

- **Token refresh** handles at most 40 grants per 30-minute run (one Twitch subrequest each; the free plan caps a
  run at 50). The soonest-expiring go first and the buffer is an hour, so a backlog drains before anything expires.
- **`pubsubCheckedAt`** in `/healthz` is when the PubSub verdict last *changed*, not when it was last checked (an
  unchanged verdict is not rewritten, to save a D1 write on every publish).
- **The credential check** runs every 6 hours; there is no 10-second-after-boot check because a Worker has no boot.
  `pubsub` is `unknown` until the first cron or the first real publish.
- Chunked uploads that declare no `Content-Length` are cut off when read, not as they stream.
