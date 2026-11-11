import { createApp } from './app';
import type { Env } from './env';
import { runScheduled } from './scheduled';

const app = createApp();

export default {
  fetch: app.fetch,
  scheduled(controller: ScheduledController, env: Env, ctx: ExecutionContext): void {
    ctx.waitUntil(runScheduled(env, controller.cron));
  },
} satisfies ExportedHandler<Env>;
