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
| `Twitch:ClientId` | `Twitch__ClientId` | Twitch application Client ID (used for both the OAuth login flow and PubSub). |
| `Twitch:ClientSecret` | `Twitch__ClientSecret` | Twitch application Client Secret, used for the `/oauth/authorize` + `/oauth/callback` Authorization Code flow. **Never commit this.** |
| `Twitch:OAuthRedirectUri` | `Twitch__OAuthRedirectUri` | The EBS's own redirect URI, exactly as registered in the Twitch Developer Console (e.g. `https://ebs.example.com/oauth/callback`). |
| `Twitch:OAuthScopes` | `Twitch__OAuthScopes__0`, `...__1`, ... | Scopes requested from Twitch during login. Default `user:read:email`. |
| `Twitch:ExtensionId` | `Twitch__ExtensionId` | The Twitch Extension's Client ID. |
| `Twitch:ExtensionSecret` | `Twitch__ExtensionSecret` | Base64-encoded Extension Secret from the Twitch Developer Console. **Never commit this.** |
| `Ebs:Port` | `Ebs__Port` | HTTP port Kestrel listens on when `ASPNETCORE_URLS` isn't set. Default `8787`. Note that a launch profile's `applicationUrl` *is* `ASPNETCORE_URLS`, so running from an IDE takes its port from `Properties/launchSettings.json` and ignores this setting — which is why that file is committed, pinned to 8787 to match the registered OAuth redirect URI. |
| `Ebs:MaxStatePayloadBytes` | `Ebs__MaxStatePayloadBytes` | Max serialized state size forwarded to PubSub. Default `5000` (Twitch's hard limit is 5 KiB). |
| `Ebs:UpdateStateRateLimit` / `Ebs:UpdateStateRateLimitWindowSeconds` | `Ebs__UpdateStateRateLimit` / `Ebs__UpdateStateRateLimitWindowSeconds` | Per-channel rate limit applied to `POST /api/update-state`. Default 1 request / 2 seconds. |
| `Ebs:OAuthSessionTtlMinutes` | `Ebs__OAuthSessionTtlMinutes` | How long a commander has to complete the Twitch consent page before the login session expires. Default 10 minutes. |
| `Ebs:OAuthCodeTtlSeconds` | `Ebs__OAuthCodeTtlSeconds` | How long the one-time authorization code handed to the desktop client is redeemable at `/oauth/token`. Default 60 seconds. |
| `Ebs:StorageProvider` | `Ebs__StorageProvider` | `Sqlite` (default) persists state across restarts; `InMemory` is for tests and throwaway local runs only. |
| `Ebs:DataDirectory` | `Ebs__DataDirectory` | Directory holding the SQLite database `ebs.db`. Relative paths resolve against the content root. Default `data` (`/data` in the container). |
| `Ebs:DataProtectionKeysDirectory` | `Ebs__DataProtectionKeysDirectory` | Data Protection key ring used to encrypt Twitch tokens at rest. Default `{DataDirectory}/keys`; the compose file uses a separate `/keys` volume. An existing ring in the default location is moved here on startup. |
| `Ebs:ChannelStateMaxAgeHours` | `Ebs__ChannelStateMaxAgeHours` | Oldest snapshot `GET /api/initial-state` serves. Older ones are treated as gone and pruned, so a card whose clear never arrived does not stay public forever. The desktop app refreshes an unchanged card every 6 hours, so a live card never reaches it. Default `24`; `0` disables the limit; anything else under `12` is refused at startup. |
| `Ebs:TwitchTokenRefreshIntervalMinutes` / `Ebs:TwitchTokenRefreshBufferMinutes` | `Ebs__TwitchTokenRefreshIntervalMinutes` / `Ebs__TwitchTokenRefreshBufferMinutes` | How often the background loop checks broadcasters' Twitch grants, and how far ahead of expiry it refreshes them. Defaults 30 / 60 minutes. |

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
   (best-effort) the underlying Twitch grant. The clear comes first so that if it fails (`5xx`), the
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
- **Body**: `{ "state": { ... arbitrary JSON state payload ... } }`
- **Behavior**: validates the serialized state is under the configured size limit, caches it for
  `GET /api/initial-state`, and forwards it to `POST https://api.twitch.tv/helix/extensions/pubsub`
  signed with a short-lived "external" role JWT minted from the Extension Secret (the Extensions
  platform's own JWT scheme — unrelated to broadcaster authentication described above).
- **Responses**: `200 OK` on success, `401 Unauthorized` for a missing/invalid/revoked token,
  `413 Payload Too Large` if the state exceeds the size limit, `429 Too Many Requests` if
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
The desktop app queues clears and revokes the EBS did not acknowledge (in its settings) and retries
them with backoff, including after a restart.

Every `429` from the EBS carries a `Retry-After` header in seconds.

### `GET /api/initial-state/{channelId}`

Called by the extension frontend on load so it doesn't have to wait for the next PubSub event.
Returns the last state payload published for that channel, or `404` if none has been published yet,
the card was switched off, or the snapshot is older than `Ebs:ChannelStateMaxAgeHours`.
Unauthenticated but rate-limited per caller IP.

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

The keys are **not** encrypted at rest on Linux, so restrict access to that volume and its backups
as you would a password store. Back the two volumes up separately.

The schema version is stamped in `PRAGMA user_version`; the EBS migrates older files on startup and
refuses to start against a file from a newer build.

## Deployment

CI publishes an image to `ghcr.io/signal-thread-llc/ednexus-ebs` on every change to this project,
and proves the container actually starts and answers `/healthz` before calling the build good — this
service validates its Twitch configuration at startup, so "the image built" and "the service runs"
are different claims.

```sh
cd src/EDNexus.Ebs
cp .env.example .env        # then fill in ClientSecret and ExtensionSecret
docker compose up -d
curl http://localhost:8787/healthz
```

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
