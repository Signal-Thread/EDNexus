import { iso, type Deps } from '../context';

/** What the EBS last learned about whether Twitch accepts the credentials it signs PubSub calls with. */
export type CredentialState = 'unknown' | 'accepted' | 'rejected';

export interface CredentialStatus {
  state: CredentialState;
  /** The HTTP status behind a rejection (401 or 403). */
  httpStatus: number | null;
  /** When the state last changed. */
  at: string | null;
}

/** Response header on a failure caused by Twitch refusing the EBS's credentials. Mirrored by the desktop's StreamStateApiClient. */
export const REJECTED_HEADER = 'X-EDNexus-Pubsub-Rejected';

const KEY = 'pubsub_credentials';

/** True for the statuses Twitch uses to refuse a signed token: the credentials, not the payload or Twitch itself. */
export function isCredentialRefusal(httpStatus: number): boolean {
  return httpStatus === 401 || httpStatus === 403;
}

export async function getCredentialStatus(deps: Deps): Promise<CredentialStatus> {
  const row = await deps.db
    .prepare('SELECT value, updated_at FROM service_status WHERE key = ?')
    .bind(KEY)
    .first<{ value: string; updated_at: string }>();
  if (!row) return { state: 'unknown', httpStatus: null, at: null };
  const parsed = JSON.parse(row.value) as { state: CredentialState; httpStatus: number | null };
  return { state: parsed.state, httpStatus: parsed.httpStatus, at: row.updated_at };
}

// Written only when the verdict changes. Every real broadcast reports success, and an unconditional
// write per publish would cost a D1 row write for no new information.
// This is health bookkeeping, so a failed write is logged and swallowed: it must never turn a publish or
// a sign-out that Twitch already answered into an error.
async function write(deps: Deps, state: CredentialState, httpStatus: number | null): Promise<void> {
  try {
    await deps.db
      .prepare(
        `INSERT INTO service_status (key, value, updated_at) VALUES (?, ?, ?)
         ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at
         WHERE service_status.value != excluded.value`,
      )
      .bind(KEY, JSON.stringify({ state, httpStatus }), iso(deps.now()))
      .run();
  } catch (error) {
    console.warn(`Could not record the PubSub credential status: ${error instanceof Error ? error.message : error}`);
  }
}

export const markCredentialsAccepted = (deps: Deps) => write(deps, 'accepted', null);
export const markCredentialsRejected = (deps: Deps, httpStatus: number) => write(deps, 'rejected', httpStatus);
