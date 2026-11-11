import type { Config } from './config';
import type { Env } from './env';

/** Everything a store or route needs: bindings, validated config, and an injectable clock. */
export interface Deps {
  env: Env;
  db: D1Database;
  config: Config;
  now: () => Date;
}

/** Fixed-width ISO-8601 UTC text; compares correctly as text. */
export function iso(date: Date): string {
  return date.toISOString();
}
