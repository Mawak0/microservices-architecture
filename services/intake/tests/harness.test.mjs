import test from 'node:test';
import assert from 'node:assert/strict';
import { once } from 'node:events';
import { createFixtureServer } from '../test-support/fixtures.mjs';
import { request, pngBase64 } from '../test-support/client.mjs';
import { inflateSync } from 'node:zlib';
import { PNG } from 'pngjs';
import jsQR from 'jsqr';

test('Сценарий сохраняет привязку задачи и поддерживает поздний приём без нового цикла', async t => {
  const server = createFixtureServer(); server.listen(0, '127.0.0.1'); await once(server, 'listening');
  t.after(() => new Promise(resolve => server.close(resolve)));
  const base = `http://127.0.0.1:${server.address().port}`;
  const call = (method, path, body) => request(base, method, path, { body });
  const s = (await call('POST', '/__test/scenarios', { system: 'custom-game', submissionOpen: false,
    appealsAllowed: true, modifiersAllowed: false, templateKey: 'custom/layout-2' })).data;
  const b = (await call('POST', '/blank-verifications', { qrToken: s.qrTokens[2] })).data;
  assert.equal(b.taskId, s.taskIds[1]); assert.equal(b.taskNumber, '2');
  assert.equal((await call('GET', `/tasks/${b.taskId}`)).data.referenceAnswer, '5');
  assert.equal(s.game.capabilities.appealsAllowed, true); assert.equal(s.game.appealLimit, undefined);
  const template = await call('GET', '/material-templates/custom%2Flayout-2');
  assert.equal(template.data.key, 'custom/layout-2');
  assert.ok(template.data.answerSheet.some(b => b.field === 'modifierLabel'));
  await call('PATCH', `/__test/scenarios/${s.game.id}`, { lateSubmissionAllowed: true });
  const changed = (await call('GET', `/games/${s.game.id}`)).data;
  assert.equal(changed.id, s.game.id); assert.equal(changed.admissionVersion, s.game.admissionVersion + 1);
  assert.equal(changed.appealDeadline, s.game.appealDeadline);
  assert.equal(changed.submissionDeadline, s.game.submissionDeadline);
  assert.equal(changed.lateSubmissionAllowed, true); assert.ok(Date.parse(changed.submissionDeadline) < Date.now());
  assert.equal(changed.cycleId, undefined);
  assert.equal((await call('POST', '/blank-verifications', { qrToken: s.qrTokens[2] })).data.taskId, b.taskId);
  const late = { admissionVersion: changed.admissionVersion, acceptedAt: new Date().toISOString() };
  assert.equal((await call('PATCH', `/__test/receipts/${s.receipts[2].id}`, late)).status, 200);
  const receipt = (await call('GET', `/receipts/${s.receipts[2].id}`)).data;
  assert.equal(receipt.admissionVersion, late.admissionVersion); assert.equal(receipt.acceptedAt, late.acceptedAt);
  assert.equal(receipt.taskId, b.taskId);
  assert.equal((await call('PATCH', `/__test/receipts/${receipt.id}`, { ...late, taskId: 'other' })).status, 422);
});

test('PNG тестовых запросов имеет корректные CRC и сжатый поток', () => {
  const image = Buffer.from(pngBase64, 'base64');
  assert.equal(image.subarray(0, 8).toString('hex'), '89504e470d0a1a0a');
  const chunks = [];
  for (let offset = 8; offset < image.length;) {
    const length = image.readUInt32BE(offset), data = image.subarray(offset + 4, offset + 8 + length);
    let crc = 0xffffffff;
    for (const byte of data) {
      crc ^= byte;
      for (let bit = 0; bit < 8; bit++) crc = (crc >>> 1) ^ ((crc & 1) ? 0xedb88320 : 0);
    }
    assert.equal((crc ^ 0xffffffff) >>> 0, image.readUInt32BE(offset + 8 + length));
    if (data.subarray(0, 4).toString() === 'IDAT') chunks.push(data.subarray(4));
    offset += length + 12;
  }
  assert.deepEqual([...inflateSync(Buffer.concat(chunks))], [0, 255, 255, 255, 255]);
});

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

test('Снимки содержат реальные QR, пустая PNG не декодируется', async t => {
  const server = createFixtureServer(); server.listen(0, '127.0.0.1'); await once(server, 'listening');
  t.after(() => new Promise(resolve => server.close(resolve)));
  const base = `http://127.0.0.1:${server.address().port}`;
  const s = (await request(base, 'POST', '/__test/scenarios', { body: {} })).data;
  const decode = base64 => {
    const png = PNG.sync.read(Buffer.from(base64, 'base64'));
    return jsQR(new Uint8ClampedArray(png.data), png.width, png.height)?.data;
  };
  for (let i = 0; i < s.qrTokens.length; i++) assert.equal(decode(s.blankImages[i]), s.qrTokens[i]);
  assert.equal(decode(s.invalidQrImage), s.invalidQrToken);
  assert.equal(decode(pngBase64), undefined);
});

test('Часы управляемы; альтернативная задача не заменяет исходную', async t => {
  const server = createFixtureServer(); server.listen(0, '127.0.0.1'); await once(server, 'listening');
  t.after(() => new Promise(resolve => server.close(resolve)));
  const base = `http://127.0.0.1:${server.address().port}`;
  const call = (method, path, body) => request(base, method, path, { body });
  const before = Date.parse((await call('GET', '/clock')).data.now);
  const after = Date.parse((await call('POST', '/__test/clock/advance', { milliseconds: 60000 })).data.now);
  assert.ok(after >= before + 60000);
  assert.equal((await call('POST', '/__test/clock/advance', { milliseconds: -1 })).status, 422);
  const s = (await call('POST', '/__test/scenarios', {})).data;
  const original = (await call('GET', `/tasks/${s.taskIds[0]}`)).data;
  const alternative = (await call('POST', '/__test/alternative-tasks', { taskId: original.id, referenceAnswer: '5' })).data;
  assert.notEqual(alternative.id, original.id);
  assert.deepEqual((await call('GET', `/tasks/${original.id}`)).data, original);
  assert.deepEqual((await call('GET', `/games/${s.game.id}`)).data.taskIds, s.taskIds);
});
