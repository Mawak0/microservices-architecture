import test from 'node:test';
import assert from 'node:assert/strict';
import { once } from 'node:events';
import { createFixtureServer } from '../test-support/fixtures.mjs';
import { request } from '../test-support/client.mjs';

test('Заглушки изолируют сценарии, проверяют QR, имитируют ошибки хранения и событий', async t => {
  const server = createFixtureServer(); server.listen(0, '127.0.0.1'); await once(server, 'listening');
  t.after(() => new Promise(resolve => server.close(resolve)));
  const base = `http://127.0.0.1:${server.address().port}`;
  const call = (method, path, body) => request(base, method, path, { body });
  const a = (await call('POST', '/__test/scenarios', {})).data;
  const b = (await call('POST', '/__test/scenarios', { system: 'domino' })).data;
  assert.notEqual(a.game.id, b.game.id); assert.equal(a.qrTokens.length, 8);
  assert.equal((await call('POST', '/auth/introspect', { token: 'test-multi' })).data.roles.length, 2);
  assert.equal((await call('POST', '/auth/introspect', { token: 'unknown' })).data.active, false);
  assert.equal((await call('POST', '/blank-verifications', { qrToken: a.qrTokens[0] })).data.teamId, a.teamId);
  assert.equal((await call('POST', '/blank-verifications', { qrToken: 'forged' })).status, 422);
  await call('PATCH', `/__test/scenarios/${a.game.id}`, { storageAvailable: false });
  assert.equal((await call('POST', '/images', { blankId: a.blankIds[0] })).status, 503);
  const event = { eventId: 'e1', type: 'Example', occurredAt: new Date().toISOString(), aggregateId: 'a', data: { gameId: a.game.id } };
  await call('PUT', '/__test/event-sink', { available: false });
  assert.equal((await call('POST', '/events', event)).status, 503);
  await call('PUT', '/__test/event-sink', { available: true });
  assert.equal((await call('POST', '/events', event)).status, 202);
  assert.equal((await call('POST', '/events', event)).status, 202);
  assert.equal((await call('GET', `/__test/events?gameId=${a.game.id}`)).data.length, 1);
  assert.equal((await call('GET', `/__test/events?gameId=${b.game.id}`)).data.length, 0);
});
