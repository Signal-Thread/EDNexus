# EDNexus.Ebs — Twitch Extension Backend Service

A small, standalone ASP.NET Core service that bridges the EDNexus desktop app (running on the
broadcaster's machine) and the Twitch Extension frontend (running in viewers' browsers). The
desktop app cannot talk to the extension frontend directly (CORS, TLS, and firewall constraints on
a sandboxed iframe), so it POSTs state updates here; the EBS verifies the caller and relays the
update to viewers via Twitch's Extensions PubSub API.

The EBS is also the desktop app's *only* path to Twitch login: it is the registered Twitch
application (holds the client id + secret), performs the real Twitch OAuth 2.0 Authorization Code
handshake on the broadcaster's behalf, and issues its own long-lived opaque token back to the
desktop client. The desktop app never talks to `*.twitch.tv` directly and never sees a raw Twitch
access/refresh token — see "OAuth login flow" below.

The extension frontend that consumes these updates lives in [`extension/`](../../extension/README.md),
along with the payload contract it expects.

## Running locally

```sh
dotnet run --project src/EDNexus.Ebs
```

Configuration can be supplied via `appsettings.json`, `appsettings.Development.json` (git-ignored),
environment variables, or `dotnet user-secrets` — standard ASP.NET Core configuration precedence
applies.

**Put the two secrets in user-secrets, not in a file.** `Twitch:ClientSecret` and
`Twitch:ExtensionSecret` are the credentials that let anyone act as this extension; user-secrets
keeps them outside the working tree entirely, where no `git add -A` can reach them:

```powershell
dotnet user-secrets --project src/EDNexus.Ebs set "Twitch:ClientSecret" "<client secret>"
dotnet user-secrets --project src/EDNexus.Ebs set "Twitch:ExtensionSecret" "<base64 extension secret>"
```

The client id and extension id are public identifiers — they travel in OAuth URLs and in the
extension's own frontend — so those are fine in `appsettings.json`.

| Setting | Environment variable | Description |
|---|---|---|
| `Twitch:ClientId` | `Twitch__ClientId` | Twitch application Client ID, used for the OAuth login flow. Required. |
| `Twitch:ClientSecret` | `Twitch__ClientSecret` | Twitch application Client Secret, used for the `/oauth/authorize` + `/oauth/callback` Authorization Code flow. **Never commit this.** |
| `Twitch:OAuthRedirectUri` | `Twitch__OAuthRedirectUri` | The EBS's own redirect URI, exactly as registered in the Twitch Developer Console (e.g. `https://ebs.example.com/oauth/callback`). |
| `Twitch:OAuthScopes` | `Twitch__OAuthScopes__0`, `...__1`, ... | Scopes requested from Twitch during login. Default `user:read:email`. |
| `Twitch:ExtensionId` | `Twitch__ExtensionId` | The Twitch Extension's Client ID, sent as the `Client-Id` header of the Helix PubSub call. Required. Often the same value as `Twitch:ClientId` — see [Client ID vs Extension ID](#client-id-vs-extension-id). |
| `Twitch:OwnerUserId` | `Twitch__OwnerUserId` | Optional. The numeric Twitch user id of the extension's **owner**, used as the `user_id` claim of the PubSub JWT (Twitch documents that claim as the owner's id). Unset keeps the historical placeholder value. See the [publish smoke test](#publish-smoke-test). |
| `Twitch:ExtensionSecret` | `Twitch__ExtensionSecret` | Base64-encoded Extension Secret from the Twitch Developer Console (at least 16 bytes once decoded; Twitch issues 32). **Never commit this.** |
| `Ebs:Port` | `Ebs__Port` | HTTP port Kestrel listens on when `ASPNETCORE_URLS` isn't set. Default `8787`. Note that a launch profile's `applicationUrl` *is* `ASPNETCORE_URLS`, so running from an IDE takes its port from `Properties/launchSettings.json` and ignores this setting — which is why that file is committed, pinned to 8787 to match the registered OAuth redirect URI. |
| `Ebs:MaxStatePayloadBytes` | `Ebs__MaxStatePayloadBytes` | Max serialized (compact) state size forwarded to PubSub. Default `5000`; values above `5120` (Twitch's hard 5 KiB limit) are refused at startup. |
| `Ebs:MaxRequestBodyBytes` | `Ebs__MaxRequestBodyBytes` | Largest request body `POST/DELETE /api/update-state` and the `/oauth/*` endpoints read; larger gets `413` without being buffered. Default `16384`. |
| `Ebs:TrustedProxies` / `Ebs:TrustedProxyNetworks` | `Ebs__TrustedProxies__0` / `Ebs__TrustedProxyNetworks__0` | Whose `X-Forwarded-For` is believed: exact proxy IPs / CIDR ranges. Both empty (the default) trusts loopback and the private ranges. See [Behind a reverse proxy](#behind-a-reverse-proxy). |
| `Ebs:InitialStateRateLimit` / `Ebs:InitialStateRateLimitWindowSeconds` | `Ebs__InitialStateRateLimit` / `Ebs__InitialStateRateLimitWindowSeconds` | Per-client-IP limit on `GET /api/initial-state`. Default 60 per 10 seconds. |
| `Ebs:InitialStateCacheSeconds` | `Ebs__InitialStateCacheSeconds` | `max-age` of the public `Cache-Control` on a served snapshot. Default `5`; `0` disables it. |
| `Ebs:OAuthRateLimit` / `Ebs:OAuthRateLimitWindowSeconds` | `Ebs__OAuthRateLimit` / `Ebs__OAuthRateLimitWindowSeconds` | Per-client-IP limit shared by the four `/oauth/*` routes. Default 30 per 60 seconds. |
| `Ebs:ChannelStatePruneIntervalMinutes` | `Ebs__ChannelStatePruneIntervalMinutes` | How often expired snapshots are deleted from the database. Default 15. |
| `Ebs:AdditionalAllowedFrontendOrigins` | `Ebs__AdditionalAllowedFrontendOrigins__0` | Extra browser origins allowed to read `GET /api/initial-state` (Developer Rig, local asset server). **Production: leave this unset.** Twitch's own `*.ext-twitch.tv` is always allowed; every extra origin is one more website that can read your viewers' card data from a browser. |
| `Ebs:UpdateStateRateLimit` / `Ebs:UpdateStateRateLimitWindowSeconds` | `Ebs__UpdateStateRateLimit` / `Ebs__UpdateStateRateLimitWindowSeconds` | Per-channel rate limit applied to `POST /api/update-state`. Default 1 request / 2 seconds. |
| `Ebs:OAuthSessionTtlMinutes` | `Ebs__OAuthSessionTtlMinutes` | How long a commander has to complete the Twitch consent page before the login session expires. Default 10 minutes. |
| `Ebs:OAuthCodeTtlSeconds` | `Ebs__OAuthCodeTtlSeconds` | How long the one-time authorization code handed to the desktop client is redeemable at `/oauth/token`. Default 60 seconds. |
| `Ebs:StorageProvider` | `Ebs__StorageProvider` | `Sqlite` (default) persists state across restarts; `InMemory` is for tests and throwaway local runs only. |
| `Ebs:DataDirectory` | `Ebs__DataDirectory` | Directory holding the SQLite database `ebs.db`. Relative paths resolve against the content root. Default `data` (`/data` in the container). |
| `Ebs:DataProtectionKeysDirectory` | `Ebs__DataProtectionKeysDirectory` | Data Protection key ring used to encrypt Twitch tokens at rest. Default `{DataDirectory}/keys`; the compose file uses a separate `/keys` volume. An existing ring in the default location is moved here on startup. |
| `Ebs:ChannelStateMaxAgeHours` | `Ebs__ChannelStateMaxAgeHours` | Oldest snapshot `GET /api/initial-state` serves. Older ones are treated as gone and are deleted by a timer, so a card whose clear never arrived does not stay public forever. The desktop app re-sends an unchanged card every 10 minutes while the game runs (older builds: every 6 hours), so a live card never reaches it. Default `24`; `0` disables the limit; anything else under `12` is refused at startup. See [Privacy](#privacy-the-24-hour-snapshot-window). |
| `Ebs:TwitchTokenRefreshIntervalMinutes` / `Ebs:TwitchTokenRefreshBufferMinutes` | `Ebs__TwitchTokenRefreshIntervalMinutes` / `Ebs__TwitchTokenRefreshBufferMinutes` | How often the background loop checks broadcasters' Twitch grants, and how far ahead of expiry it refreshes them. Defaults 30 / 60 minutes. |

The service validates its configuration at startup and refuses to run on: a missing `ClientId`,
`ClientSecret`, `ExtensionId` or `ExtensionSecret`; an `ExtensionSecret` that is not base64 or
decodes to under 16 bytes; an `OAuthRedirectUri` that is not an absolute `https` URL (`http` is
accepted for `localhost` only); a non-numeric `OwnerUserId`; `MaxStatePayloadBytes` above 5120;
`ChannelStateMaxAgeHours` between 1 and 11; or an unparseable proxy address/CIDR. The error names
the setting.

### Client ID vs Extension ID

Two different Twitch objects can be involved, and they are used for different calls:

- **`Twitch:ClientId` (+ `ClientSecret`)** is the Twitch *application* that performs the OAuth login
  (`/oauth/authorize`, `/oauth/callback`, token refresh, revoke). Its redirect URI is the one registered
  as `Twitch:OAuthRedirectUri`.
- **`Twitch:ExtensionId` (+ `ExtensionSecret`)** is the *extension*. Twitch's "Send Extension PubSub
  Message" call is made on the extension's behalf: the JWT is signed with the Extension Secret and the
  `Client-Id` header is the extension's Client ID, so that is what the EBS sends.

For this project's deployment they are the same value (the extension's Client ID is also the OAuth
application), which is why `.env.example` repeats it. If yours differ, set both. If `ExtensionId` is
left empty the service refuses to start rather than guess.

`Twitch:OwnerUserId` is the numeric user id of the account that *owns* the extension. Twitch documents
the `user_id` claim of the PubSub JWT as the extension owner's id. When unset the EBS sends the
placeholder it always has (`ednexus_ebs`), which has worked for this extension; if publishing ever
starts failing with `401`/`403` from Twitch, set `Twitch__OwnerUserId` (look the id up from the owner's
login via the Helix `Get Users` endpoint).

### Publish smoke test

After any change to the Twitch settings, or on a new deployment, prove that PubSub publishing works
end to end instead of finding out when a viewer reports a blank card:

1. Run the service with the real secrets, with the extension installed and activated on a test
   channel you own, and the extension frontend open on that channel (or in the Developer Rig's
   viewer).
2. Sign in from the desktop app, which exercises the full OAuth flow, then
   publish a card (or `curl -X POST .../api/update-state -H "Authorization: Bearer <token>" -H "Content-Type: application/json" -d '{"state":{"v":1,"headline":"EBS smoke test"}}'`).
3. Expect `200 {"published":true,...}`. A `502` means Twitch refused the PubSub call: check the EBS
   log line `Twitch PubSub broadcast for channel ... failed with <status>`. `401`/`403` usually means a
   wrong `ExtensionSecret`, a `Client-Id` that is not the extension's, or the wrong `user_id` (set
   `Twitch:OwnerUserId`); `400` means the payload.
4. Confirm the card appears on the open extension, then `curl .../api/initial-state/<channel id>`
   returns the same state.
5. Switch the card off (`DELETE /api/update-state`, or the app's toggle) and confirm the card disappears
   and `initial-state` answers `404`.

## Behind a reverse proxy

The container speaks plain HTTP and expects a TLS-terminating proxy (Caddy in the compose file) in
front of it. Behind a proxy the TCP peer is the proxy, so the EBS reads the client address from
`X-Forwarded-For` — but only when the peer is a trusted proxy, and only one hop (the entry the
proxy itself appended; anything a client prepended is ignored). Without that, every viewer would share
the proxy's single rate-limit bucket.

- By default the loopback and private ranges (`127.0.0.0/8`, `::1`, `10.0.0.0/8`, `172.16.0.0/12`,
  `192.168.0.0/16`, `fc00::/7`) are trusted as proxies, which covers a proxy container on the same
  Docker network.
- To be exact, set `Ebs__TrustedProxyNetworks__0=172.18.0.0/16` (your compose network) or
  `Ebs__TrustedProxies__0=<proxy ip>`. Setting either replaces the defaults entirely.
- A request whose peer is **not** a trusted proxy has its forwarded headers ignored, so a client that
  reaches the port directly cannot pick its own rate-limit bucket. This is why the compose file
  publishes the port on `127.0.0.1` only.
- The proxy must *set* `X-Forwarded-For` (Caddy's `reverse_proxy` does). Do not put a second,
  untrusted proxy or CDN in front of it without adding that hop's network, or every viewer behind it
  shares one address.

## Rate limits and request limits

Every `429` carries `Retry-After` (seconds) and `Cache-Control: no-store`; the CORS policy exposes
`Retry-After` so the extension's script can read it.

| Route | Limit | Keyed on |
|---|---|---|
| `POST /api/update-state` | `Ebs:UpdateStateRateLimit` per `Ebs:UpdateStateRateLimitWindowSeconds` (default 1 / 2 s) | authenticated channel |
| `DELETE /api/update-state` | 10 per minute | authenticated channel |
| `GET /api/initial-state/{channelId}` | `Ebs:InitialStateRateLimit` per window (default 60 / 10 s) | client IP (forwarded) |
| `GET /oauth/authorize`, `GET /oauth/callback`, `POST /oauth/token`, `POST /oauth/revoke` | `Ebs:OAuthRateLimit` per window, **shared** by the four routes (default 30 / 60 s) | client IP (forwarded) |

Request bodies on `/api/update-state` and `/oauth/*` are capped at `Ebs:MaxRequestBodyBytes` (16 KiB).
On `/oauth/authorize`, `redirect_uri` and `state` are capped at 512 characters and `code_challenge`
must be the 43-character base64url S256 value. Pending logins and unredeemed authorization codes live
in memory only: expired ones are swept, and at most 5000 of each are held (the entry closest to expiry
is dropped beyond that).

`GET /api/initial-state` answers `200` with `Cache-Control: public, max-age=5` (and `Vary: Origin`
from the CORS layer), so a CDN or browser absorbs a burst of viewers; live changes arrive over PubSub,
so a few seconds of staleness is invisible. `404`s and `429`s are not cacheable.

## OAuth login flow

The desktop app never talks to Twitch directly. Instead:

1. **`GET /oauth/authorize`** — the desktop app opens the commander's browser here with its own
   loopback `redirect_uri`, a CSRF `state`, and a PKCE `code_challenge` (`code_challenge_method=S256`).
   The EBS records these against a short-lived session and redirects the browser to Twitch's real
   consent page, using the EBS's own registered `redirect_uri` and the session id as Twitch's `state`.
2. **`GET /oauth/callback`** — Twitch redirects back here after the commander approves/denies. The
   EBS exchanges the code for a Twitch token grant using its client secret, looks up the
   broadcaster's Twitch identity, and redirects the browser to the desktop's original loopback
   `redirect_uri` with a one-time authorization `code` and the desktop's own `state` — never a raw
   Twitch token.
3. **`POST /oauth/token`** — the desktop's loopback listener has the code; it exchanges it here
   (`{ "code", "code_verifier", "redirect_uri" }`), proving possession of the PKCE verifier. On
   success the EBS mints a long-lived opaque token mapped server-side to the channel id and the
   underlying Twitch access/refresh tokens, and returns `{ "token", "channelId", "username" }`. This
   is the only token the desktop client ever stores.
4. **`POST /oauth/revoke`** — logout: `Authorization: Bearer <ebs-token>` clears the channel's
   stored card (the same way `DELETE /api/update-state` does), then revokes the EBS token and
   (best-effort) the underlying Twitch access token. (Twitch documents its revoke endpoint as taking the
   access token only; the refresh token is deleted with the record and left to expire on Twitch's side.) The clear comes first so that if it fails (`5xx`), the
   token still works for the client to retry. An unknown token gets `200`: there is nothing to do.

A background service refreshes each broadcaster's Twitch access token ahead of expiry using their
stored refresh token, so the commander stays logged in across a multi-day gap without re-auth. If a
refresh ever fails (e.g. the commander revoked access on Twitch), the broadcaster's EBS token is
marked invalid and `/api/update-state` starts rejecting it with `401`, so the desktop client knows
to prompt login again.

## API

### `POST /api/update-state`

Called by the desktop client on behalf of the broadcaster.

- **Auth**: `Authorization: Bearer <ebs-token>` — the long-lived, per-broadcaster token minted by
  `POST /oauth/token` above. The EBS resolves it server-side to a channel id; the channel id is
  never taken from the request body, so a compromised client cannot spoof another broadcaster's
  channel. Returns `401` if the token is unknown or its underlying Twitch grant has been marked
  invalid (prompting the desktop client to log in again).
- **Body**: `{ "state": { ... JSON object ... } }`. A missing, `null` or non-object `state` is a `400`.
- **Behavior**: validates the state is under the configured size limit — measured on its compact
  serialisation, which is also exactly what is stored and broadcast — caches it for
  `GET /api/initial-state`, and forwards it to `POST https://api.twitch.tv/helix/extensions/pubsub`
  signed with a short-lived "external" role JWT minted from the Extension Secret (the Extensions
  platform's own JWT scheme — unrelated to broadcaster authentication described above).
- **Responses**: `200 OK` on success, `401 Unauthorized` for a missing/invalid/revoked token,
  `400 Bad Request` for a missing/`null`/non-object `state`, `413 Payload Too Large` if the state
  or the request body exceeds its limit, `429 Too Many Requests` if
  the per-channel rate limit is exceeded, `502 Bad Gateway` if Twitch PubSub rejects the message.

### `DELETE /api/update-state`

Called by the desktop client when the broadcaster switches the card off. Same bearer auth as the
`POST`, except that a lapsed Twitch grant is not required to be valid: a broadcaster must always be
able to take their card down. It deletes the stored snapshot, so `GET /api/initial-state` answers `404` again, and
broadcasts `{ "v": 1, "offline": true }` so viewers who are already watching hide the card. Returns
`204`, or `502` with an `X-EDNexus-Snapshot-Removed: true` header if PubSub did not deliver that
broadcast (the snapshot is removed either way, so the client can safely retry; the header tells it
so, since a proxy's `502` would not carry it). `POST /oauth/revoke` revokes the token even when that broadcast fails. It has its own per-channel limit (10 per minute), so a clear sent straight after a publish is
never rejected by that publish's window. `POST /oauth/revoke` does the same clear on sign-out.
A clear is **idempotent**: repeating it for a card that is already gone (the desktop sends one on game
shutdown, on app exit and at launch) is a quiet `204`. A publish and a clear/sign-out for the same
channel are serialised, and a publish re-checks its token once it holds the channel, so a publish that
was already in flight when the broadcaster signed out can never store a snapshot afterwards.
The desktop app queues clears and revokes the EBS did not acknowledge (in its settings) and retries
them with backoff, including after a restart.

Every `429` from the EBS carries a `Retry-After` header in seconds.

### `GET /api/initial-state/{channelId}`

Called by the extension frontend on load so it doesn't have to wait for the next PubSub event.
Returns the last state payload published for that channel, or `404` if none has been published yet,
the card was switched off, or the snapshot is older than `Ebs:ChannelStateMaxAgeHours`.
Unauthenticated, rate-limited per client IP, and cacheable for a few seconds (see
[Rate limits](#rate-limits-and-request-limits)).

### `GET /`

Redirects to the project site, https://signal-thread-llc.github.io/EDNexus/.

### `GET /healthz`

Liveness probe for container/serverless hosting.

## Persistence

State that has to survive a crash, restart, redeploy or host reboot is kept in a single SQLite file,
`{Ebs:DataDirectory}/ebs.db` (WAL mode, `synchronous=FULL`):

| State | Stored | Notes |
|---|---|---|
| Broadcaster tokens + the Twitch grants they wrap | `broadcaster_tokens` | The EBS bearer token is stored only as a SHA-256 hash; Twitch access/refresh tokens are ASP.NET Core Data Protection ciphertext. |
| Each channel's last published state | `channel_state` | So `GET /api/initial-state/{channelId}` still answers after a restart. |
| Pending `/oauth/authorize` sessions and one-time auth codes | memory only | Minutes/seconds-lived. A restart mid-login just means clicking "Log in" again. |

The Data Protection key ring (`Ebs:DataProtectionKeysDirectory`, default `{DataDirectory}/keys`)
must persist as long as the database does. If it's lost, stored grants can't be decrypted: those
broadcasters get `401` and have to log in again, and the EBS keeps running.

Keep the key ring on a **different volume** from the database. The shipped `docker-compose.yml`
does this (`ebs-data` at `/data`, `ebs-keys` at `/keys`). With both on one volume, anyone who can
read a copy or backup of that volume can decrypt every broadcaster's Twitch grant. EBS bearer
tokens are stored only as hashes, so they can't be recovered either way.

When `Ebs:DataProtectionKeysDirectory` points somewhere other than the default and that directory
has no keys yet, the EBS moves an existing ring from `{DataDirectory}/keys` into it on startup. So
switching an existing deployment to a separate key volume doesn't log anyone out. If both
directories hold keys, it uses the configured one and leaves the old one alone, with a warning.

The keys are **not** encrypted at rest (ASP.NET Core Data Protection only encrypts keys on Windows,
with DPAPI, and this container runs on Linux), so restrict access to that volume and its backups as you
would a password store. Back the two volumes up separately. Encrypting the ring with a certificate
(`ProtectKeysWithCertificate`) is possible but not wired up here: it needs a certificate and password
supplied to the container and every restore. Treat the separate-volume layout as the protection.

The schema version is stamped in `PRAGMA user_version`; the EBS migrates older files on startup and
refuses to start against a file from a newer build. Version 2 adds an index on
`channel_state.updated_at` (for the periodic prune). **Rolling back** to a build older than that one
after the new build has opened the volume is refused by the old build ("schema version 2, newer than this
build supports"): roll forward instead, or restore `ebs-data` from a backup.

## Privacy: the 24-hour snapshot window

`GET /api/initial-state/{channelId}` is unauthenticated by design (viewers have no credential), and the
snapshot behind it is the card the broadcaster chose to show. Normally it is removed the moment the
card is switched off, the game shuts down, the app exits or the broadcaster signs out. If every one of
those clears is lost (the machine lost power, the network was down for good), the last snapshot stays
publicly readable until it is older than `Ebs:ChannelStateMaxAgeHours` (default **24 hours**), after
which it is treated as gone immediately and deleted by the next prune pass. The desktop app's
10-minute heartbeat keeps a live card well inside that limit. Lower the limit (not below 12) if that
window is too long for your users; `0` disables it and keeps abandoned snapshots indefinitely.

## Deployment

CI publishes an image to `ghcr.io/signal-thread-llc/ednexus-ebs` on every change to this project,
and proves the container actually starts and answers `/healthz` before calling the build good — this
service validates its Twitch configuration at startup, so "the image built" and "the service runs"
are different claims.

```sh
cd src/EDNexus.Ebs
cp .env.example .env        # then fill in ClientSecret and ExtensionSecret
docker compose up -d
curl http://127.0.0.1:8787/healthz
```

The compose file publishes the port on `127.0.0.1` only: the service speaks plain HTTP, so it must not
be reachable from the network except through the TLS proxy. To expose it directly (not recommended),
change the mapping to `"8787:8787"` deliberately. With the Caddy service enabled, drop the `ports`
block altogether and let Caddy reach `ebs:8787` over the compose network.

`docker-compose.yml` is a starting point rather than a finished deployment: it runs the published
image with the settings from `.env`, and leaves TLS to a reverse proxy (there is a commented Caddy
service showing the shape). That split matters because Twitch requires the OAuth redirect URI to be
`https` and to match `Twitch:OAuthRedirectUri` exactly — so the **proxy's** public hostname is what
gets registered with Twitch, not this container's port.

The service is also small enough to host on a serverless container platform (Azure Container Apps,
Fly.io, etc.). Wherever it runs, mount a persistent volume at `/data` (see [Persistence](#persistence)).

**Production caveat:** SQLite suits the single EBS instance we deploy. A multi-instance
(horizontally scaled) deployment needs a shared backing store, e.g. Postgres or Redis, behind
`IBroadcasterTokenStore` / `IChannelStateStore`, plus a shared Data Protection key ring.
