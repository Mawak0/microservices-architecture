import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';

export async function runRestartProbe({ serviceBase, fixtureBase, restart }) {
  async function send(base, method, path, body, token, idempotencyKey = randomUUID()) {
    const headers = { 'content-type': 'application/json', 'idempotency-key': idempotencyKey };
    if (token) headers.authorization = `Bearer ${token}`;
    const response = await fetch(`${base}${path}`, { method, headers,
      body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(10000) });
    return { status: response.status, body: await response.json() };
  }
  const fixture = (method, path, body) => send(fixtureBase, method, path, body);
  const created = await fixture('POST', '/__test/scenarios', {}); assert.equal(created.status, 201);
  const scenario = created.body;
  assert.equal((await send(serviceBase, 'PUT', `/intake-sessions/${scenario.game.id}`,
    { admissionVersion: scenario.game.admissionVersion }, 'test-admin')).status, 200);
  const command = { qrToken: scenario.qrTokens[0], imageBase64: scenario.blankImages[0],
    imageMediaType: 'image/png', legibilityConfirmed: true };
  await fixture('PUT', '/__test/event-sink', { available: false });
  const key = randomUUID();
  const accepted = await send(serviceBase, 'POST', '/receipts', command, 'test-observer', key);
  assert.equal(accepted.status, 201);
  const denied = await send(serviceBase, 'POST', '/receipts', command, 'test-checker', key);
  assert.equal(denied.status, 403); assert.equal(denied.body.code, 'FORBIDDEN');
  const anonymous = await send(serviceBase, 'POST', '/receipts', command, undefined, key);
  assert.equal(anonymous.status, 401); assert.equal(anonymous.body.code, 'UNAUTHENTICATED');
  await restart();
  const saved = await send(serviceBase, 'GET', `/receipts/${accepted.body.id}`, undefined, 'test-system');
  assert.equal(saved.status, 200); assert.deepEqual(saved.body, accepted.body);
  const replay = await send(serviceBase, 'POST', '/receipts', command, 'test-observer', key);
  assert.equal(replay.status, 200); assert.deepEqual(replay.body, accepted.body);
  const duplicate = await send(serviceBase, 'POST', '/receipts', command, 'test-observer');
  assert.equal(duplicate.status, 409); assert.equal(duplicate.body.code, 'BLANK_ALREADY_ACCEPTED');
  const session = await send(serviceBase, 'GET', `/intake-sessions/${scenario.game.id}`, undefined, 'test-admin');
  assert.deepEqual(session.body.receiptIds, [accepted.body.id]);
  await fixture('PUT', '/__test/event-sink', { available: true });
  let published;
  const deadline = Date.now() + 10000;
  do {
    published = (await fixture('GET', `/__test/events?gameId=${scenario.game.id}`)).body;
    if (published.length) break;
    await new Promise(resolve => setTimeout(resolve, 100));
  } while (Date.now() < deadline);
  assert.equal(published.length, 1); assert.equal(published[0].data.receiptId, accepted.body.id);
  process.stdout.write('Runtime restart probe passed: authorization, durable receipt, duplicate, idempotency and pending outbox.\n');
}
