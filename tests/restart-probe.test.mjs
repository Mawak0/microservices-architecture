import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import test from 'node:test';
import { runRestartProbe } from '../scripts/restart-probe.mjs';

test('Restart probe expires a persisted delivery lease using fixture time, not wall-clock waiting', async t => {
  const accepted = { id: 'accepted-answer', gameId: 'game' };
  let submitted = false, restarted = false, sinkAvailable = true, elapsed = 0;
  const deliveries = [];
  const server = createServer(async (request, response) => {
    let raw = ''; for await (const chunk of request) raw += chunk;
    const body = raw ? JSON.parse(raw) : undefined;
    let status = 200, result = {};
    const path = new URL(request.url, 'http://fixture').pathname;
    if (path === '/__test/scenarios') {
      status = 201; result = { game: { id: 'game', admissionVersion: 1 }, teamId: 'team', qrTokens: ['qr'], blankImages: ['png'] };
    } else if (path === '/__test/event-sink') {
      sinkAvailable = body.available;
    } else if (path === '/__test/clock/advance') {
      assert.equal(restarted, true); elapsed += body.milliseconds;
    } else if (path === '/__test/events') {
      if (sinkAvailable && restarted && elapsed >= 30000 && !deliveries.length)
        deliveries.push({ eventId: 'stable-event', data: { receiptId: accepted.id } });
      result = deliveries;
    } else if (path === '/receipts' && request.method === 'POST') {
      const identity = request.headers.authorization;
      if (!identity) { status = 401; result = { code: 'UNAUTHENTICATED' }; }
      else if (identity === 'Bearer test-checker') { status = 403; result = { code: 'FORBIDDEN' }; }
      else if (submitted && request.headers['idempotency-key'] !== acceptedKey) { status = 409; result = { code: 'BLANK_ALREADY_ACCEPTED' }; }
      else {
        status = submitted ? 200 : 201; result = accepted;
        if (!submitted) { acceptedKey = request.headers['idempotency-key']; submitted = true; }
      }
    } else if (path === '/receipts/accepted-answer') result = accepted;
    else if (path === '/intake-sessions/game') result = { receiptIds: submitted ? [accepted.id] : [] };
    else { status = 404; result = { unexpected: path }; }
    response.writeHead(status, { 'content-type': 'application/json' }); response.end(JSON.stringify(result));
  });
  let acceptedKey;
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(() => new Promise(resolve => server.close(resolve)));
  const base = `http://127.0.0.1:${server.address().port}`;
  await runRestartProbe({ serviceBase: base, fixtureBase: base, restart: async () => {
    restarted = true;
    assert.equal(sinkAvailable, false);
    assert.deepEqual(deliveries, []);
    // Restarting alone must not bypass the lease owned by the stopped process.
    assert.equal(elapsed, 0);
  } });
  assert.equal(elapsed, 31000);
  assert.equal(deliveries.length, 1);
  assert.equal(deliveries[0].eventId, 'stable-event');
});
