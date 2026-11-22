import { createServer } from 'node:http';
import { fileURLToPath } from 'node:url';
import { app } from './app.mjs';
import { connectIdentity } from './oidc.mjs';
import { settings } from './settings.mjs';

export async function start(env, listen = createServer, connect = connectIdentity) {
  const config = settings(env);
  const identity = await connect(config.oidc);
  const root = fileURLToPath(new URL('../dist', import.meta.url));
  return listen(app(config, identity, { root })).listen(config.port, env.STAFF_BIND ?? '127.0.0.1');
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  await start(process.env);
}
