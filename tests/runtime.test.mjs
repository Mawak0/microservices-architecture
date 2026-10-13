import { test, before } from 'node:test';
import assert from 'node:assert/strict';
import { api, scenario, key, expectStatus, expectError, events, eventually, fixture, clockNow, advanceClock, requireService } from '../test-support/client.mjs';
before(requireService);

async function assignedWork(kind) {
  const s = await scenario();
  expectStatus(await api('PUT', '/appeal-quotas/' + s.game.id, { body: { limit: 2 } }), 200);
  const { id, ...data } = s.receipts[0];
  const c = expectStatus(await api('POST', '/integrations/receipts', { role: 'system', body: {
    eventId: key(), type: 'ReceiptAccepted', aggregateId: id, occurredAt: await clockNow(), data: { receiptId: id, ...data }
  } }), 201);
  const assigned = expectStatus(await api('POST', '/checks/' + c.id + '/claim', { role: 'checker' }), 200);
  if (kind === 'checks') return { s, id: c.id, check: assigned, path: '/checks/' + c.id };
  expectStatus(await api('POST', '/checks/' + c.id + '/verdict', { role: 'checker',
    assignmentToken: assigned.lease.token, body: { verdict: 'incorrect' } }), 200);
  const appeal = expectStatus(await api('POST', '/appeals', { role: 'observer',
    body: { checkId: c.id, reason: 'По просьбе участника' } }), 201);
  const claimed = expectStatus(await api('POST', '/appeals/' + appeal.id + '/claim', { role: 'checker' }), 200);
  return { s, id: appeal.id, check: claimed.check, path: '/appeals/' + appeal.id };
}
async function getQuota(s) { return expectStatus(await api('GET', '/appeal-quotas/' + s.game.id), 200); }
async function expireLease(lease) {
  await advanceClock(Math.max(1, Date.parse(lease.expiresAt) - Date.parse(await clockNow()) + 1));
}
for (const kind of ['checks', 'appeals']) {
  test('Administrator releases another executor in ' + kind + ' without changing verdict or quota', async () => {
    const work = await assignedWork(kind), { path, check, s } = work;
    const beforeQuota = await getQuota(s), options = { assignmentToken: check.lease.token };
    expectError(await api('POST', path + '/release', { ...options, role: 'checker-2' }), 409, 'CHECK_NOT_ASSIGNED_TO_YOU');
    expectError(await api('POST', path + '/release', { ...options, role: 'checker-2', body: { mayReleaseOtherOwner: true } }),
      409, 'CHECK_NOT_ASSIGNED_TO_YOU');
    expectError(await api('POST', path + '/renew', { ...options, role: 'admin' }), 409, 'CHECK_NOT_ASSIGNED_TO_YOU');
    if (kind === 'checks') {
      expectError(await api('POST', path + '/verdict', { ...options, role: 'admin', body: { verdict: 'correct' } }),
        409, 'CHECK_NOT_ASSIGNED_TO_YOU');
    } else {
      expectError(await api('POST', path + '/resolve', { ...options, role: 'admin', body: {
        outcome: 'upheld', reason: 'Администратор не назначен проверяющим'
      } }), 409, 'CHECK_NOT_ASSIGNED_TO_YOU');
    }
    const released = expectStatus(await api('POST', path + '/release', { ...options, role: 'admin' }), 200);
    const current = kind === 'appeals' ? released.check : released;
    assert.equal(current.lease, null); assert.equal(current.assignedTo, null);
    assert.equal(current.status, kind === 'appeals' ? 'appeal-pending' : 'pending');
    assert.deepEqual(current.history, check.history); assert.equal(current.verdict, check.verdict);
    assert.deepEqual(await getQuota(s), beforeQuota);
    if (kind === 'appeals') assert.equal(released.status, 'pending');
    const claimed = expectStatus(await api('POST', path + '/claim', { role: 'checker-2' }), 200);
    const next = kind === 'appeals' ? claimed.check : claimed;
    assert.notEqual(next.lease.token, check.lease.token);
    expectError(await api('POST', path + '/release', { ...options, role: 'admin' }), 409, 'STALE_ASSIGNMENT');
    assert.deepEqual(await getQuota(s), beforeQuota);
  });
  test('Administrator cannot release missing, expired or fenced token in ' + kind, async () => {
    const { path, check, s } = await assignedWork(kind), beforeQuota = await getQuota(s);
    expectError(await api('POST', path + '/release', { role: 'admin' }), 422, 'VALIDATION_ERROR');
    await expireLease(check.lease);
    const options = { role: 'admin', assignmentToken: check.lease.token };
    expectError(await api('POST', path + '/release', options), 409, 'ASSIGNMENT_EXPIRED');
    expectStatus(await api('POST', path + '/claim', { role: 'checker-2' }), 200);
    expectError(await api('POST', path + '/release', options), 409, 'STALE_ASSIGNMENT');
    assert.deepEqual(await getQuota(s), beforeQuota);
    const state = expectStatus(await api('GET', path, { role: 'checker' }), 200);
    assert.deepEqual((kind === 'appeals' ? state.check : state).history, check.history);
  });
}
test('Documented appeal query protects server reference and preserves the original case', async () => {
  const { path, check } = await assignedWork('appeals');
  for (const role of ['admin', 'checker', 'system']) {
    const result = expectStatus(await api('GET', path, { role }), 200);
    assert.equal(result.check.originalVerdict, check.originalVerdict);
    assert.equal(result.check.imageRef, check.imageRef); assert.equal(result.check.referenceAnswer, '4');
  }
  expectError(await api('GET', path, { role: null }), 401, 'UNAUTHENTICATED');
  expectError(await api('GET', path, { role: 'organizer' }), 403, 'FORBIDDEN');
  expectError(await api('GET', path, { role: 'observer' }), 403, 'FORBIDDEN');
});
test('Automatic completion publishes after last verdict without completion GET', async () => {
  const s = await scenario({ submissionOpen: false, appealOpen: false });
  const { id, ...data } = s.receipts[0];
  const c = expectStatus(await api('POST', '/integrations/receipts', { role: 'system', body: {
    eventId: key(), type: 'ReceiptAccepted', aggregateId: id, occurredAt: await clockNow(), data: { receiptId: id, ...data }
  } }), 201);
  expectStatus(await api('POST', '/integrations/intake-closures', { role: 'system', body: {
    eventId: key(), type: 'IntakeClosed', aggregateId: s.game.id, occurredAt: await clockNow(),
    data: { gameId: s.game.id, admissionVersion: s.game.admissionVersion, receiptIds: [id] }
  } }), 200);
  const assigned = expectStatus(await api('POST', '/checks/' + c.id + '/claim', { role: 'checker' }), 200);
  expectStatus(await api('POST', '/checks/' + c.id + '/verdict', { role: 'checker',
    assignmentToken: assigned.lease.token, body: { verdict: 'correct' } }), 200);
  const completed = await eventually(() => events(s.game.id, 'AllChecksCompleted'), e => e.length === 1);
  assert.deepEqual(completed[0].data.receiptIds, [id]);
});
test('Automatic completion observes appeal deadline transition without completion GET', async () => {
  const s = await scenario({ submissionOpen: false, appealOpen: true });
  expectStatus(await api('POST', '/integrations/intake-closures', { role: 'system', body: {
    eventId: key(), type: 'IntakeClosed', aggregateId: s.game.id, occurredAt: await clockNow(),
    data: { gameId: s.game.id, admissionVersion: s.game.admissionVersion, receiptIds: [] }
  } }), 200);
  assert.equal((await events(s.game.id, 'AllChecksCompleted')).length, 0);
  expectStatus(await fixture('PATCH', '/__test/scenarios/' + s.game.id, { appealOpen: false }), 200);
  const completed = await eventually(() => events(s.game.id, 'AllChecksCompleted'), e => e.length === 1);
  assert.deepEqual(completed[0].data.receiptIds, []);
});
