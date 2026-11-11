-- EDNexus EBS schema (ported from the SQLite file the ASP.NET service kept in ebs.db).
-- Timestamps are fixed-width ISO-8601 UTC text (Date.toISOString), so they compare correctly as text.

-- Long-lived broadcaster tokens and the Twitch grants they wrap. The EBS bearer token is stored only
-- as a SHA-256 hash; the Twitch access/refresh tokens are AES-GCM ciphertext (see src/crypto.ts).
CREATE TABLE broadcaster_tokens (
    channel_id            TEXT PRIMARY KEY,
    token_hash            TEXT NOT NULL UNIQUE,
    username              TEXT NOT NULL,
    twitch_access_token   TEXT NOT NULL,
    twitch_refresh_token  TEXT NOT NULL,
    twitch_expires_at     TEXT NOT NULL,
    is_twitch_grant_valid INTEGER NOT NULL,
    created_at            TEXT NOT NULL,
    last_refreshed_at     TEXT NULL
);

-- The last state each channel published, served by GET /api/initial-state/{channelId}.
CREATE TABLE channel_state (
    channel_id TEXT PRIMARY KEY,
    state_json TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
CREATE INDEX ix_channel_state_updated_at ON channel_state (updated_at);

-- Pending /oauth/authorize sessions (kind 'session') and one-time authorization codes (kind 'code').
-- The .NET service held these in process memory; Worker isolates share none, so they live here, short
-- lived. A 'code' payload carries Twitch tokens and is AES-GCM ciphertext, never plaintext.
CREATE TABLE oauth_pending (
    id         TEXT PRIMARY KEY,
    kind       TEXT NOT NULL,
    payload    TEXT NOT NULL,
    expires_at TEXT NOT NULL
);
CREATE INDEX ix_oauth_pending_expires_at ON oauth_pending (expires_at);

-- Fixed-window counters for the per-channel limits (update-state, clear-state).
CREATE TABLE rate_limit (
    key          TEXT PRIMARY KEY,
    window_start INTEGER NOT NULL,
    count        INTEGER NOT NULL
);

-- Small key/value status rows (whether Twitch accepts the PubSub credentials).
CREATE TABLE service_status (
    key        TEXT PRIMARY KEY,
    value      TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
