import { describe, it, before } from 'node:test';
import assert from 'node:assert/strict';
import { api, fixture, scenario, requireService, expectStatus, expectError, key, eventually, events, pngBase64 } from '../test-support/client.mjs';

const open = s => api('PUT', `/intake-sessions/${s.game.id}`, { body: { cycleId: s.game.cycleId } });
async function setup(options) { const s = await scenario(options); expectStatus(await open(s), 200); return s; }
const submit = (s, index = 0, changes = {}, options = {}) => api('POST', '/receipts', { role: 'observer', body: {
  qrToken: s.qrTokens[index], imageBase64: pngBase64, imageMediaType: 'image/png', legibilityConfirmed: true, ...changes
}, ...options });
const close = (s, changes = {}) => api('POST', `/intake-sessions/${s.game.id}/close`, { role: 'system', body: { cycleId: s.game.cycleId, ...changes } });
const expire = s => fixture('PATCH', `/__test/scenarios/${s.game.id}`, { submissionOpen: false });

describe('ТЗ: приём бланков', { concurrency: false }, () => {
  before(requireService);
  it('I01 подтверждение содержит сохранённый снимок и проверенную принадлежность', async () => {
    const s = await setup(), r = expectStatus(await submit(s), 201);
    assert.equal(r.blankId, s.blankIds[0]); assert.equal(r.teamId, s.teamId); assert.equal(r.gameId, s.game.id);
    assert.equal(r.cycleId, s.game.cycleId); assert.ok(r.imageRef); assert.ok(Number.isFinite(Date.parse(r.acceptedAt)));
    assert.deepEqual(expectStatus(await api('GET', `/receipts/${r.id}`), 200), r);
    const images = expectStatus(await fixture('GET', `/__test/images?gameId=${s.game.id}`), 200);
    assert.ok(images.some(i => i.imageRef === r.imageRef && i.dataBase64 === pngBase64));
  });
  it('I02 неподлинный QR отклоняется без принятия ответа', async () => {
    const s = await setup();
    expectError(await submit(s, 0, { qrToken: `${s.qrTokens[0]}tampered` }), 422, 'INVALID_QR');
    assert.equal((await events(s.game.id, 'ReceiptAccepted')).length, 0);
    expectStatus(await submit(s), 201);
  });
  it('I03 чёткость должна быть подтверждена наблюдателем', async () => {
    const s = await setup(); expectError(await submit(s, 0, { legibilityConfirmed: false }), 422, 'LEGIBILITY_NOT_CONFIRMED');
    expectStatus(await submit(s), 201);
  });
  it('I04 второй запрос на тот же бланк отклоняется', async () => {
    const s = await setup(); expectStatus(await submit(s), 201);
    expectError(await submit(s), 409, 'BLANK_ALREADY_ACCEPTED');
  });
  it('I05 из шести конкурентных запросов принимается ровно один', async () => {
    const s = await setup(), results = await Promise.all(Array.from({ length: 6 }, () => submit(s)));
    assert.equal(results.filter(r => r.status === 201).length, 1);
    for (const r of results.filter(r => r.status !== 201)) expectError(r, 409, 'BLANK_ALREADY_ACCEPTED');
    const list = await eventually(() => events(s.game.id, 'ReceiptAccepted'), e => e.length > 0);
    assert.equal(list.length, 1);
  });
  it('I06 повтор ключа не создаёт вторую квитанцию, другое тело конфликтует', async () => {
    const s = await setup(), idempotencyKey = key();
    const a = expectStatus(await submit(s, 0, {}, { idempotencyKey }), 201);
    assert.deepEqual(expectStatus(await submit(s, 0, {}, { idempotencyKey }), [200, 201]), a);
    expectError(await submit(s, 1, {}, { idempotencyKey }), 409, 'IDEMPOTENCY_KEY_REUSED');
  });
  it('I07 актуальный дедлайн запрещает новый приём даже до доставки команды закрытия', async () => {
    const s = await setup(); await expire(s);
    expectError(await submit(s), 409, 'INTAKE_CLOSED');
  });
  it('I08 недопущенная команда не сдаёт ответ', async () => {
    const s = await setup({ admitted: false }); expectError(await submit(s), 409, 'TEAM_NOT_ADMITTED');
  });
  it('I09 сбой хранилища не подтверждает приём и допускает последующую успешную сдачу', async () => {
    const s = await setup({ storageAvailable: false });
    expectError(await submit(s), 503, 'STORAGE_UNAVAILABLE');
    assert.equal((await events(s.game.id, 'ReceiptAccepted')).length, 0);
    await fixture('PATCH', `/__test/scenarios/${s.game.id}`, { storageAvailable: true });
    expectStatus(await submit(s), 201);
  });
  it('I10 временный отказ доставки не теряет ответ и событие', async t => {
    const s = await setup();
    t.after(() => fixture('PUT', '/__test/event-sink', { available: true }));
    await fixture('PUT', '/__test/event-sink', { available: false });
    const r = expectStatus(await submit(s), 201);
    expectStatus(await api('GET', `/receipts/${r.id}`), 200);
    await fixture('PUT', '/__test/event-sink', { available: true });
    const list = await eventually(() => events(s.game.id, 'ReceiptAccepted'), e => e.some(e => e.data.receiptId === r.id));
    const e = list.find(e => e.data.receiptId === r.id);
    assert.equal(e.data.imageRef, r.imageRef); assert.equal(e.data.blankId, r.blankId);
    assert.equal(e.data.cycleId, r.cycleId); assert.equal(e.data.teamId, r.teamId);
    assert.ok(!JSON.stringify(e).includes(pngBase64), 'Событие не должно нести тяжёлое фото');
  });
  it('I11 закрытие фиксирует полный список и запрещает новые ответы', async () => {
    const s = await setup(); const accepted = [];
    for (let i = 0; i < 3; i++) accepted.push(expectStatus(await submit(s, i), 201));
    await expire(s); const result = expectStatus(await close(s), 200);
    assert.equal(result.status, 'closed'); assert.deepEqual([...result.receiptIds].sort(), accepted.map(r => r.id).sort());
    expectError(await submit(s, 3), 409, 'INTAKE_CLOSED');
    const list = await eventually(() => events(s.game.id, 'IntakeClosed'), e => e.length > 0);
    assert.deepEqual([...list[0].data.receiptIds].sort(), accepted.map(r => r.id).sort());
    assert.equal(list[0].data.cycleId, s.game.cycleId);
  });
  it('I12 manifest учитывает ответы, ещё не доставленные на проверку', async t => {
    const s = await setup(); t.after(() => fixture('PUT', '/__test/event-sink', { available: true }));
    await fixture('PUT', '/__test/event-sink', { available: false });
    const r = expectStatus(await submit(s), 201); await expire(s);
    assert.deepEqual(expectStatus(await close(s), 200).receiptIds, [r.id]);
  });
  it('I13 роль проверяющего или организатора не разрешает сдачу', async () => {
    const s = await setup();
    for (const role of ['checker', 'organizer']) expectError(await submit(s, 0, {}, { role }), 403, 'FORBIDDEN');
    expectError(await submit(s, 0, {}, { role: null }), 401, 'UNAUTHENTICATED');
  });
  it('I14 администратор и пользователь с несколькими ролями могут принимать', async () => {
    const s = await setup();
    expectStatus(await submit(s, 0, {}, { role: 'admin' }), 201);
    expectStatus(await submit(s, 1, {}, { role: 'multi' }), 201);
  });
  it('I15 клиент не может подменять доверенную принадлежность', async () => {
    const s = await setup();
    expectError(await submit(s, 0, { teamId: s.otherTeamId, gameId: key() }), 422, 'VALIDATION_ERROR');
    assert.equal(expectStatus(await submit(s), 201).teamId, s.teamId);
  });
  it('I16 пустое и повреждённое изображение не принимается', async () => {
    const s = await setup();
    for (const imageBase64 of ['', 'not-valid-base64!', Buffer.from('not an image').toString('base64')])
      expectError(await submit(s, 0, { imageBase64 }), 422, 'INVALID_IMAGE');
  });
  it('I17 квитанции доступны проверяющему, но не организатору/гостю', async () => {
    const s = await setup(), r = expectStatus(await submit(s), 201);
    expectStatus(await api('GET', `/receipts/${r.id}`, { role: 'checker' }), 200);
    expectError(await api('GET', `/receipts/${r.id}`, { role: 'organizer' }), 403, 'FORBIDDEN');
    expectError(await api('GET', `/receipts/${r.id}`, { role: null }), 401, 'UNAUTHENTICATED');
  });
  it('I18 бонусный бланк принимается без распознанных номера задачи и ответа', async () => {
    const s = await setup({ system: 'bonuses' }); expectStatus(await submit(s), 201);
  });
  it('I19 старый cycleId не закрывает актуальный сеанс', async () => {
    const s = await setup(); await expire(s);
    expectError(await close(s, { cycleId: key() }), 409, 'CYCLE_MISMATCH');
    expectStatus(await close(s), 200);
  });
  it('I20 закрытый цикл не открывается повторным PUT', async () => {
    const s = await setup(); await expire(s); expectStatus(await close(s), 200);
    await fixture('PATCH', `/__test/scenarios/${s.game.id}`, { submissionOpen: true });
    expectError(await open(s), 409, 'INTAKE_CLOSED');
    expectError(await submit(s), 409, 'INTAKE_CLOSED');
  });
  it('I21 приём не требует синхронного вердикта и не содержит его', async () => {
    const s = await setup(), r = expectStatus(await submit(s), 201);
    assert.equal(r.verdict, undefined); assert.equal(r.score, undefined);
  });
  it('I22 техническое закрытие до наступления срока отклоняется', async () => {
    const s = await setup(); expectError(await close(s), 409, 'DEADLINE_NOT_REACHED');
  });
});
