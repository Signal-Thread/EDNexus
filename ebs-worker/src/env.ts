/** Bindings of the EBS Worker. Keep this, wrangler.example.jsonc, .dev.vars.example and README.md in step. */
export interface Env {
  DB: D1Database;

  // Optional Rate Limiting bindings (per-client-IP limits). Absent in tests and when not configured.
  RL_INITIAL_STATE?: RateLimit;
  RL_OAUTH?: RateLimit;

  // Secrets.
  TWITCH_CLIENT_ID: string;
  TWITCH_CLIENT_SECRET: string;
  TWITCH_EXTENSION_ID: string;
  /** base64 Extension Secret from the Twitch Developer Console; signs the PubSub JWT. */
  TWITCH_EXTENSION_SECRET: string;
  /** base64 of 32 random bytes; encrypts the Twitch tokens stored in D1. Lose it and every broadcaster logs in again. */
  TOKEN_ENCRYPTION_KEY: string;

  // Vars (all optional except the redirect URI, which has a default).
  TWITCH_OAUTH_REDIRECT_URI?: string;
  TWITCH_OAUTH_SCOPES?: string;
  TWITCH_OWNER_USER_ID?: string;
  MAX_STATE_PAYLOAD_BYTES?: string;
  MAX_REQUEST_BODY_BYTES?: string;
  UPDATE_STATE_RATE_LIMIT?: string;
  UPDATE_STATE_RATE_LIMIT_WINDOW_SECONDS?: string;
  INITIAL_STATE_CACHE_SECONDS?: string;
  OAUTH_SESSION_TTL_MINUTES?: string;
  OAUTH_CODE_TTL_SECONDS?: string;
  TWITCH_TOKEN_REFRESH_BUFFER_MINUTES?: string;
  CHANNEL_STATE_MAX_AGE_HOURS?: string;
  ADDITIONAL_ALLOWED_FRONTEND_ORIGINS?: string;
}

/** The Workers Rate Limiting binding. */
export interface RateLimit {
  limit(options: { key: string }): Promise<{ success: boolean }>;
}
