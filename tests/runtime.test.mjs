import { test, before } from 'node:test';
import assert from 'node:assert/strict';
import { api, scenario, key, expectStatus, events, eventually, fixture, clockNow, requireService } from '../test-support/client.mjs';
before(requireService);
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
