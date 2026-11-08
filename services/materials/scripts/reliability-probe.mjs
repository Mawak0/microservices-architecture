import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { request, expectStatus, expectError, eventually } from '../test-support/client.mjs';
import { validateResponse } from '../test-support/schemas.mjs';

function calls(service, fixture) {
  return {
    api: async (method, path, options) => { const result = await request(service, method, path, options); validateResponse(method, path, result); return result; },
    fixture: (method, path, body) => request(fixture, method, path, { body, role: null })
  };
}
export async function beforeRestart(service, fixture) {
  const call = calls(service, fixture);
  const scenario = expectStatus(await call.fixture('POST', '/__test/scenarios', {}), 201);
  await call.fixture('PUT', '/__test/event-sink', { available: false });
  const key = randomUUID(), body = { gameId: scenario.game.id, teamId: scenario.teamId, taskId: scenario.taskIds[0], count: 1 };
  const batch = expectStatus(await call.api('POST', '/blanks', { body, idempotencyKey: key }), 201);
  const kit = expectStatus(await call.api('POST', '/kits', { body: { gameId: scenario.game.id, teamId: scenario.teamId } }), 201);
  // These rejected commands exercise actual transport binding and role gates, not a replacement implementation.
  expectError(await call.api('POST', '/blanks', { body: { ...body, unexpected: true } }), 422, 'VALIDATION_ERROR');
  expectError(await call.api('POST', '/blanks', { body: { ...body, modifier: { kind: 'add' } } }), 422, 'VALIDATION_ERROR');
  expectError(await call.api('GET', `/kits/${kit.id}`, { role: 'system' }), 403, 'FORBIDDEN');
  await call.fixture('PATCH', `/__test/scenarios/${scenario.game.id}`, { admitted: false, templateKey: 'changed/layout' });
  return { scenario, key, body, batch, kit };
}
export async function afterRestart(service, fixture, probe) {
  const call = calls(service, fixture);
  assert.deepEqual(expectStatus(await call.api('POST', '/blanks', { body: probe.body, idempotencyKey: probe.key }), 201), probe.batch,
    'Committed replay must survive process restart and revoked admission.');
  const blank = probe.batch.blanks[0];
  assert.deepEqual(expectStatus(await call.api('GET', `/blanks/${blank.id}`), 200), blank);
  assert.deepEqual(expectStatus(await call.api('POST', '/blank-verifications', { role: 'system', body: { qrToken: blank.qrToken } }), 200), blank,
    'Persisted signature key and registry must survive process restart.');
  assert.deepEqual(expectStatus(await call.api('GET', `/kits/${probe.kit.id}`), 200), probe.kit);
  expectError(await call.api('POST', '/blanks', { body: { ...probe.body, count: 2 }, idempotencyKey: probe.key }), 409, 'IDEMPOTENCY_KEY_REUSED');
  await call.fixture('PUT', '/__test/event-sink', { available: true });
  const events = await eventually(async () => expectStatus(await call.fixture('GET', `/__test/events?gameId=${probe.scenario.game.id}`), 200),
    items => items.some(e => e.type === 'BlanksIssued') && items.some(e => e.type === 'KitPrepared'));
  assert.equal(events.filter(e => e.type === 'BlanksIssued').length, 1);
  assert.deepEqual(events.find(e => e.type === 'BlanksIssued').data.blankIds, [blank.id]);
  console.log('Real process restart retained issued identities, signing key, replay, kit and undelivered outbox.');
}
