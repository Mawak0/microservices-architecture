// @vitest-environment node
import { createServer } from 'node:http';
import { once } from 'node:events';
import { expect, it, vi } from 'vitest';
import { app } from '../server/app.mjs';
import { connectIdentity } from '../server/oidc.mjs';
import { Sessions } from '../server/session.mjs';
import { settings } from '../server/settings.mjs';
import { start } from '../server/start.mjs';

const env = {
  STAFF_ORIGIN: 'https://staff.example',
  STAFF_OIDC_ISSUER: 'https://issuer.example',
  STAFF_OIDC_CLIENT_ID: 'staff-client',
  STAFF_OIDC_CLIENT_SECRET: 'fixture-only',
  STAFF_IDENTITY_SERVICE_TOKEN: 'integration',
  STAFF_IDENTITY_URL: 'https://api.example/introspect',
  STAFF_DIRECTORY_URL: 'https://api.example/tournaments',
  STAFF_MATERIALS_URL: 'http://materials/',
  STAFF_INTAKE_URL: 'http://intake/',
  STAFF_REVIEW_URL: 'http://review/',
};

it('validates deployment settings and uses the OIDC authorization code with PKCE', async () => {
  expect(settings(env).port).toBe(19184);
  expect(() => settings({})).toThrow('STAFF_ORIGIN');
  expect(() => settings({ ...env, STAFF_ORIGIN: 'http://example.net' })).toThrow('HTTPS');
  expect(() => settings({ ...env, STAFF_ORIGIN: 'https://staff.example/app' })).toThrow('path');
  const library = {
    discovery: vi.fn(async () => ({})),
    randomPKCECodeVerifier: () => 'verifier',
    randomState: () => 'state',
    calculatePKCECodeChallenge: async () => 'challenge',
    buildAuthorizationUrl: vi.fn(() => new URL('https://issuer.example/signin')),
    authorizationCodeGrant: vi.fn(async () => ({ access_token: 'test-token' })),
  };
  const oidc = await connectIdentity(settings(env).oidc, library);
  const challenge = await oidc.begin('https://staff.example/auth/callback');
  const credentials = await oidc.finish(new URL('https://staff.example/auth/callback?code=abc'), challenge);
  expect(credentials).toEqual({ token: 'test-token', lifetime: 3600 });
  expect(library.authorizationCodeGrant.mock.calls[0][2]).toEqual({ pkceCodeVerifier: 'verifier', expectedState: 'state' });
  library.authorizationCodeGrant.mockResolvedValueOnce({});
  await expect(oidc.finish(new URL('https://staff.example/auth/callback'), challenge)).rejects.toThrow('Missing access token');
  const listen = vi.fn();
  await start(env, () => ({ listen }), async () => oidc);
  expect(listen).toHaveBeenCalledWith(19184, '127.0.0.1');
});

it('drops expired sessions and caps unattended handshakes', () => {
  let now = 0;
  const sessions = new Sessions(() => now, 1);
  const key = sessions.create('mine', 1);
  expect(sessions.get(key)).toBe('mine');
  expect(() => sessions.create('too many', 1)).toThrow();
  now = 1000;
  expect(sessions.get(key)).toBeUndefined();
  expect(sessions.create('next', 3)).toBeTruthy();
});

it('exchanges the code once and proxies only data for the current user and session', async () => {
  const identity = {
    begin: vi.fn(async () => ({ url: 'https://issuer.example/signin', state: 'a' })),
    finish: vi.fn(async () => ({ token: 'observer-token', lifetime: 600 })),
  };
  const send = vi.fn(async (url, options) => {
    if (url === env.STAFF_IDENTITY_URL) {
      expect(options.headers.Authorization).toBe('Bearer integration');
      return Response.json({ active: true, userId: 'olga', roles: ['observer'] });
    }
    if (url === env.STAFF_DIRECTORY_URL) return Response.json({ tournaments: [] });
    if (String(url) === 'http://intake/receipts?gameId=game') {
      expect(options.headers.get('Authorization')).toBe('Bearer observer-token');
      return new Response(null, { status: 304, headers: { ETag: '"scope"', 'Cache-Control': 'private, no-cache' } });
    }
    return Response.json({ id: 'original' }, { status: 201, headers: {
      'Operation-Location': '/operations/one', 'Operation-Status': 'AwaitingCheckpoint',
    } });
  });
  const read = vi.fn(async () => Buffer.from('test asset'));
  const config = settings(env);
  const server = createServer((req, res) => handler(req, res));
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const base = 'http://127.0.0.1:' + server.address().port;
  const handler = app(config, identity, { send, read });
  const call = (path, opts = {}) => fetch(base + path, { redirect: 'manual', ...opts });
  try {
    expect((await call('/api/session')).status).toBe(401);
    expect((await call('/auth/callback')).status).toBe(400);
    expect((await call('/')).headers.get('cache-control')).toBe('no-cache');
    expect((await call('/assets/index-abcdefgh.js')).headers.get('cache-control')).toMatch(/immutable/);
    expect((await call('/not-a-route')).status).toBe(404);
    const login = await call('/auth/login');
    expect(login.headers.get('location')).toBe('https://issuer.example/signin');
    expect(login.headers.get('set-cookie')).toMatch(/Secure/);
    const callback = await call('/auth/callback?state=a&code=1', { headers: {
      Cookie: login.headers.get('set-cookie').split(';')[0],
    } });
    const Cookie = callback.headers.getSetCookie()[0].split(';')[0];
    expect((await call('/api/session', { headers: { Cookie } })).status).toBe(200);
    expect((await call('/api/context', { headers: { Cookie } })).status).toBe(200);
    const headers = { Cookie, 'X-Staff-Actor': 'olga', Origin: 'https://staff.example' };
    expect((await call('/api/intake/receipts', { headers: { ...headers, 'X-Staff-Actor': 'ivan' } })).status).toBe(409);
    expect((await call('/api/review/integrations/receipts', { headers })).status).toBe(404);
    expect((await call('/api/intake/receipts', { method: 'POST', headers: { Cookie } })).status).toBe(403);
    const cached = await call('/api/intake/receipts?gameId=game', { headers: { ...headers, 'If-None-Match': '"scope"' } });
    expect(cached.status).toBe(304);
    expect(cached.headers.get('vary')).toMatch(/Cookie/);
    const mutation = await call('/api/intake/receipts', { method: 'POST', headers: { ...headers, 'Idempotency-Key': 'original' }, body: '{}' });
    expect(mutation.headers.get('operation-location')).toBe('/operations/one');
    expect(mutation.headers.get('cache-control')).toBe('no-store');
    expect(await mutation.json()).toEqual({ id: 'original' });
    expect((await call('/auth/logout', { method: 'POST', headers })).status).toBe(204);
    expect((await call('/api/session', { headers })).status).toBe(401);
  } finally {
    server.closeAllConnections();
    server.close();
    await once(server, 'close');
  }
});
