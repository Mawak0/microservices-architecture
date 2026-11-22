import { readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { Sessions } from './session.mjs';
import { belongsToGame, directoryContains } from './game-access.mjs';

const routes = {
  materials: new Set(['blanks', 'kits', 'print-jobs', 'operations']),
  intake: new Set(['receipts', 'operations']),
  review: new Set(['checks', 'review-queues', 'appeals', 'appeal-quotas', 'retakes', 'operations']),
};
const forwardedRequestHeaders = ['content-type', 'idempotency-key', 'assignment-token', 'if-none-match'];
const forwardedResponseHeaders = ['content-type', 'operation-id', 'operation-location', 'operation-status',
  'etag', 'content-disposition', 'retry-after'];
const assetTypes = { js: 'text/javascript; charset=utf-8', css: 'text/css; charset=utf-8', svg: 'image/svg+xml' };

function json(response, status, data) {
  response.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8' });
  response.end(JSON.stringify(data));
}

class RequestError extends Error {
  constructor(status, code) { super(code); this.status = status; }
}

function body(request) {
  return (async () => {
    const chunks = []; let size = 0;
    for await (const chunk of request) {
      size += chunk.length;
      if (size > 24 * 1024 * 1024) throw new RequestError(413, 'UPLOAD_TOO_LARGE');
      chunks.push(chunk);
    }
    return Buffer.concat(chunks);
  })();
}

function cookies(request) {
  return Object.fromEntries((request.headers.cookie ?? '').split(';').map(part => part.trim().split('=', 2)));
}

export function app(config, identity, { send = fetch, read = readFile, root = 'dist', sessions = new Sessions(), challenges = new Sessions() } = {}) {
  const secure = config.origin.protocol === 'https:';
  const prefix = secure ? '__Host-' : '';
  const sessionName = prefix + 'staff-session';
  const challengeName = prefix + 'staff-login';
  const cookie = (key, value, lifetime) =>
    key + '=' + value + '; Path=/; HttpOnly; SameSite=Lax; Max-Age=' + lifetime + (secure ? '; Secure' : '');
  const redirect = (res, url, setCookie) => {
    res.writeHead(303, { Location: url, 'Set-Cookie': setCookie });
    res.end();
  };

  async function actor(token) {
    const headers = { 'Content-Type': 'application/json' };
    if (config.identityServiceToken) headers.Authorization = 'Bearer ' + config.identityServiceToken;
    const response = await send(config.identityUrl, {
      method: 'POST', headers, body: JSON.stringify({ token }),
      redirect: 'error', signal: AbortSignal.timeout(10_000),
    });
    if (!response.ok) throw new RequestError(503, 'IDENTITY_UNAVAILABLE');
    const value = await response.json();
    if (!value.active || typeof value.userId !== 'string' || !Array.isArray(value.roles))
      throw new RequestError(401, 'UNAUTHENTICATED');
    return { id: value.userId, roles: value.roles };
  }

  function directory(token) {
    return send(config.directoryUrl, {
      headers: { Authorization: 'Bearer ' + token, 'Cache-Control': 'no-cache' },
      redirect: 'error', signal: AbortSignal.timeout(10_000),
    });
  }

  return async (request, response) => {
    response.setHeader('Cache-Control', 'no-store');
    response.setHeader('X-Content-Type-Options', 'nosniff');
    response.setHeader('Referrer-Policy', 'same-origin');
    const values = cookies(request);
    const sessionId = values[sessionName];
    const session = sessions.get(sessionId);
    const method = request.method;
    try {
      const url = new URL(request.url, config.origin);
      if (url.origin !== config.origin.origin) throw new RequestError(400, 'INVALID_PATH');
      if (method !== 'GET' && method !== 'HEAD' && request.headers.origin !== config.origin.origin)
        throw new RequestError(403, 'ORIGIN_NOT_ALLOWED');
      if (url.pathname === '/auth/login' && method === 'GET') {
        const challenge = await identity.begin(new URL('/auth/callback', config.origin).href);
        const key = challenges.create(challenge, 300);
        return redirect(response, challenge.url, cookie(challengeName, key, 300));
      }
      if (url.pathname === '/auth/callback' && method === 'GET') {
        const challenge = challenges.get(values[challengeName]);
        challenges.delete(values[challengeName]);
        if (!challenge) throw new RequestError(400, 'LOGIN_EXPIRED');
        const credentials = await identity.finish(url, challenge);
        const user = await actor(credentials.token);
        const lifetime = Math.min(credentials.lifetime, 8 * 3600);
        sessions.delete(sessionId);
        const key = sessions.create({ actorId: user.id, token: credentials.token }, lifetime);
        return redirect(response, '/', [cookie(sessionName, key, lifetime), cookie(challengeName, '', 0)]);
      }
      if (url.pathname === '/auth/logout' && method === 'POST') {
        sessions.delete(sessionId);
        response.setHeader('Set-Cookie', cookie(sessionName, '', 0));
        response.writeHead(204).end();
        return;
      }
      if (url.pathname.startsWith('/api/')) {
        if (!session) throw new RequestError(401, 'UNAUTHENTICATED');
        if (url.pathname === '/api/session' && method === 'GET')
          return json(response, 200, await actor(session.token));
        if (url.pathname === '/api/context' && method === 'GET')
          return await relay(await directory(session.token), response, false);
        const [, , service, segment, ...rest] = url.pathname.split('/');
        if (!Object.hasOwn(routes, service) || !routes[service].has(segment) ||
            rest.some(part => !/^[a-zA-Z0-9_-]+$/.test(part)) || !['GET', 'POST', 'PUT'].includes(method))
          throw new RequestError(404, 'NOT_FOUND');
        if (request.headers['x-staff-actor'] !== session.actorId)
          throw new RequestError(409, 'SESSION_CHANGED');

        const serviceBase = config.services[service].replace(/\/?$/, '/');
        const destination = new URL([segment, ...rest].join('/') + url.search, serviceBase);
        const headers = new Headers({ Authorization: 'Bearer ' + session.token });
        const bytes = method === 'GET' ? undefined : await body(request);
        if (segment === 'operations') {
          if (method !== 'GET' || rest.length !== 1) throw new RequestError(404, 'NOT_FOUND');
        } else {
          const gameId = request.headers['x-staff-game'];
          if (!gameId) throw new RequestError(403, 'GAME_ACCESS_DENIED');
          const access = await directory(session.token);
          if (access.status === 401) throw new RequestError(401, 'UNAUTHENTICATED');
          if (access.status === 403) throw new RequestError(403, 'GAME_ACCESS_DENIED');
          if (!access.ok) throw new RequestError(503, 'DIRECTORY_UNAVAILABLE');
          if (!directoryContains(await access.json(), gameId))
            throw new RequestError(403, 'GAME_ACCESS_DENIED');
          const allowed = await belongsToGame({ service, segment, rest, method, url, bytes, gameId }, async path => {
            const result = await send(new URL(path, serviceBase), {
              headers, redirect: 'error', signal: AbortSignal.timeout(8_000),
            });
            if (result.status === 401) throw new RequestError(401, 'UNAUTHENTICATED');
            if (result.status >= 500 || result.status === 429) throw new RequestError(503, 'SERVICE_UNAVAILABLE');
            return result.ok ? await result.json() : undefined;
          });
          if (!allowed) throw new RequestError(403, 'GAME_ACCESS_DENIED');
        }
        for (const name of forwardedRequestHeaders)
          if (request.headers[name]) headers.set(name, request.headers[name]);
        const remote = await send(destination, {
          method, headers, body: bytes,
          redirect: 'error', signal: AbortSignal.timeout(24_000),
        });
        return await relay(remote, response, method === 'GET');
      }
      if (method === 'GET' && url.pathname === '/') {
        const index = await read(join(root, 'index.html'));
        response.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-cache' });
        return response.end(index);
      }
      const asset = url.pathname.match(/^\/assets\/([a-zA-Z0-9_-]+-[-a-zA-Z0-9_]{8,})\.(js|css|svg)$/);
      if (asset && method === 'GET') {
        const data = await read(join(root, 'assets', asset[1] + '.' + asset[2]));
        response.writeHead(200, {
          'Content-Type': assetTypes[asset[2]], 'Cache-Control': 'public, max-age=31536000, immutable',
        });
        return response.end(data);
      }
      throw new RequestError(404, 'NOT_FOUND');
    } catch (error) {
      if (response.headersSent) return response.destroy();
      if (error instanceof RequestError) return json(response, error.status, { code: error.message });
      if (error.code === 'ENOENT') return json(response, 404, { code: 'NOT_FOUND' });
      return json(response, 503, { code: 'SERVICE_UNAVAILABLE' });
    }
  };
}

async function relay(remote, response, allowValidation) {
  for (const name of forwardedResponseHeaders) {
    const value = remote.headers.get(name);
    if (value) response.setHeader(name, value);
  }
  const cache = remote.headers.get('cache-control') ?? '';
  if (allowValidation && (remote.status === 200 || remote.status === 304) && cache.includes('private') && cache.includes('no-cache')) {
    response.setHeader('Cache-Control', 'private, no-cache');
    response.setHeader('Vary', 'Cookie, X-Staff-Actor, X-Staff-Game');
  }
  const data = await remote.arrayBuffer();
  response.writeHead(remote.status);
  response.end(Buffer.from(data));
}
