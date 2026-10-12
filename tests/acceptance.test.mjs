import { describe, it, before } from 'node:test';
import assert from 'node:assert/strict';
import { api, fixture, scenario, requireService, expectStatus, expectError, key, eventually, events } from '../test-support/client.mjs';

function receiptEvent(s, index = 0) {
  const { id, ...data } = s.receipts[index];
  return { eventId: key(), type: 'ReceiptAccepted', occurredAt: new Date().toISOString(), aggregateId: id, data: { receiptId: id, ...data } };
}
const ingest = (s, index = 0, options = {}) => api('POST', '/integrations/receipts', { role: 'system', body: receiptEvent(s, index), ...options });
const claim = (id, role = 'checker') => api('POST', `/checks/${id}/claim`, { role });
const verdict = (s, id, extra = {}, options = {}) => api('POST', `/checks/${id}/verdict`, { role: 'checker', body: {
  verdict: 'incorrect', ...extra
}, ...options });
async function checked(s, index = 0, extra = {}) {
  const c = expectStatus(await ingest(s, index), 201); expectStatus(await claim(c.id), 200);
  return expectStatus(await verdict(s, c.id, extra), 200);
}
async function setup(options = {}) {
  const s = await scenario(options);
  expectStatus(await api('PUT', `/appeal-quotas/${s.game.id}`, { body: { limit: s.options.quota } }), 200);
  return s;
}
const quota = async s => expectStatus(await api('GET', `/appeal-quotas/${s.game.id}`), 200);
const appeal = (id, options = {}) => api('POST', '/appeals', { role: 'observer', body: { checkId: id, reason: 'По просьбе участника: неверно прочитан ответ' }, ...options });
async function claimedAppeal(id) {
  const a = expectStatus(await appeal(id), 201);
  return expectStatus(await api('POST', `/appeals/${a.id}/claim`, { role: 'checker' }), 200);
}
const resolve = (id, body = {}, options = {}) => api('POST', `/appeals/${id}/resolve`, { role: 'checker', body: {
  outcome: 'jury-error-corrected', reason: 'Результат пересмотра жюри',
  ...(body.outcome === 'upheld' ? {} : { verdict: 'correct' }), ...body
}, ...options });
const expire = s => fixture('PATCH', `/__test/scenarios/${s.game.id}`, { submissionOpen: false, appealOpen: false, finishExpired: true });
const completion = async s => expectStatus(await api('GET', `/games/${s.game.id}/completion`, { role: 'system' }), 200);
const currentGame = async s => expectStatus(await fixture('GET', `/games/${s.game.id}`), 200);
const manifest = async (s, ids, admissionVersion) => api('POST', '/integrations/intake-closures', { role: 'system', body: {
  eventId: key(), type: 'IntakeClosed', aggregateId: s.game.id, occurredAt: new Date().toISOString(),
  data: { gameId: s.game.id, admissionVersion: admissionVersion ?? (await currentGame(s)).admissionVersion, receiptIds: ids }
} });

describe('ТЗ: проверка и апелляции', { concurrency: false }, () => {
  before(requireService);
  it('R01 повтор eventId и receiptId не создаёт второе дело', async () => {
    const s = await setup(), body = receiptEvent(s), options = { role: 'system', body };
    const a = expectStatus(await api('POST', '/integrations/receipts', options), 201);
    const b = expectStatus(await api('POST', '/integrations/receipts', options), [200, 201]);
    const c = expectStatus(await ingest(s), [200, 201]);
    assert.equal(a.id, b.id); assert.equal(a.id, c.id);
    const list = expectStatus(await api('GET', `/checks?gameId=${s.game.id}`, { role: 'checker' }), 200);
    assert.equal(list.items.length, 1); assert.equal(a.imageRef, s.receipts[0].imageRef);
  });
  it('R02 следующая работа назначается одним запросом', async () => {
    const s = await setup(), c = expectStatus(await ingest(s), 201);
    const next = expectStatus(await api('POST', '/checks/next', { role: 'checker', body: { gameId: s.game.id } }), 200);
    assert.equal(next.id, c.id); assert.equal(next.assignedTo, 'test-checker'); assert.equal(next.status, 'checking');
  });
  it('R03 один ответ не назначается двум людям одновременно', async () => {
    const s = await setup(), c = expectStatus(await ingest(s), 201);
    const results = await Promise.all([claim(c.id), claim(c.id, 'checker-2')]);
    assert.equal(results.filter(r => r.status === 200).length, 1);
    expectError(results.find(r => r.status !== 200), 409, 'CHECK_ALREADY_ASSIGNED');
  });
  it('R04 только назначенный проверяющий сохраняет вердикт', async () => {
    const s = await setup(), c = expectStatus(await ingest(s), 201); expectStatus(await claim(c.id), 200);
    expectError(await verdict(s, c.id, {}, { role: 'checker-2' }), 409, 'CHECK_NOT_ASSIGNED_TO_YOU');
  });
  it('R05 задача берётся из бланка, сохраняется только выбранный вердикт', async () => {
    const s = await setup(), c = await checked(s);
    assert.equal(c.taskId, s.receipts[0].taskId); assert.equal(c.taskNumber, s.receipts[0].taskNumber);
    assert.equal(c.referenceAnswer, '4'); assert.equal(c.originalVerdict, 'incorrect');
    assert.equal(c.verdict, 'incorrect'); assert.equal(c.status, 'checked'); assert.equal(c.history.length, 1);
    assert.equal(c.history[0].actorId, 'test-checker'); assert.equal(c.revision, 1);
  });
  it('R06 проверяющий не может подменить задачу или передать расшифровку', async () => {
    const s = await setup(), other = await scenario(), c = expectStatus(await ingest(s), 201);
    expectStatus(await claim(c.id), 200);
    expectError(await verdict(s, c.id, { taskId: other.taskIds[0] }), 422, 'VALIDATION_ERROR');
    expectError(await verdict(s, c.id, { transcription: '4' }), 422, 'VALIDATION_ERROR');
  });
  it('R07 вердикт уходит на оценивание, но не содержит начисленных баллов', async () => {
    const s = await setup(), c = await checked(s);
    const list = await eventually(() => events(s.game.id, 'VerdictIssued'), e => e.length > 0);
    assert.equal(list[0].data.checkId, c.id); assert.equal(list[0].data.verdict, 'incorrect');
    assert.equal(list[0].data.revision, 1); assert.equal(list[0].data.taskId, s.taskIds[0]);
    assert.equal(list[0].data.score, undefined);
  });
  it('R08 апелляция повторно использует исходный снимок и показывает первый вердикт', async () => {
    const s = await setup(), c = await checked(s), a = await claimedAppeal(c.id);
    assert.equal(a.checkId, c.id); assert.equal(a.check.imageRef, c.imageRef);
    assert.equal(a.check.originalVerdict, 'incorrect'); assert.equal(a.check.status, 'appeal-checking');
  });
  it('R09 в домино нельзя подать апелляцию', async () => {
    const s = await setup({ system: 'domino' }), c = await checked(s);
    expectError(await appeal(c.id), 409, 'APPEAL_NOT_ALLOWED'); assert.equal((await quota(s)).used, 0);
  });
  it('R10 после дедлайна подачи апелляция отклоняется', async () => {
    const s = await setup({ appealOpen: false }), c = await checked(s);
    expectError(await appeal(c.id), 409, 'APPEAL_DEADLINE_PASSED');
  });
  it('R11 до исходного вердикта обжаловать нечего', async () => {
    const s = await setup(), c = expectStatus(await ingest(s), 201);
    expectError(await appeal(c.id), 409, 'CHECK_NOT_REVIEWED');
  });
  it('R12 команды разделяют одну квоту игры', async () => {
    const s = await setup({ quota: 1 }), a = await checked(s, 0), b = await checked(s, 4);
    expectStatus(await appeal(a.id), 201);
    expectError(await appeal(b.id), 409, 'APPEAL_QUOTA_EXHAUSTED');
    assert.equal((await quota(s)).remaining, 0);
  });
  it('R13 параллельная подача не расходует больше общей квоты', async () => {
    const s = await setup({ quota: 1 }), a = await checked(s, 0), b = await checked(s, 4);
    const results = await Promise.all([appeal(a.id), appeal(b.id)]);
    assert.equal(results.filter(r => r.status === 201).length, 1);
    expectError(results.find(r => r.status !== 201), 409, 'APPEAL_QUOTA_EXHAUSTED');
    assert.equal((await quota(s)).used, 1);
  });
  it('R14 нельзя обжаловать то же задание команды через другое дело', async () => {
    const s = await setup({ quota: 3 }), a = await checked(s, 0), b = await checked(s, 1);
    expectStatus(await appeal(a.id), 201);
    expectError(await appeal(a.id), 409, 'APPEAL_ALREADY_USED');
    expectError(await appeal(b.id), 409, 'APPEAL_ALREADY_USED');
    assert.equal((await quota(s)).used, 1);
  });
  it('R15 исправление возвращает ровно одно использование даже при повторе запроса', async () => {
    const s = await setup({ quota: 1 }), c = await checked(s), a = await claimedAppeal(c.id), idempotencyKey = key();
    assert.equal((await quota(s)).remaining, 0);
    const first = expectStatus(await resolve(a.id, {}, { idempotencyKey }), 200);
    assert.equal(first.check.originalVerdict, 'incorrect'); assert.equal(first.check.verdict, 'correct');
    assert.equal(first.check.history.length, 2); assert.equal(first.check.revision, 2);
    expectStatus(await resolve(a.id, {}, { idempotencyKey }), 200);
    expectError(await resolve(a.id), 409, 'APPEAL_ALREADY_RESOLVED');
    assert.equal((await quota(s)).remaining, 1); assert.equal((await quota(s)).used, 0);
    const returns = await eventually(() => events(s.game.id, 'AppealUsageReturned'), e => e.length > 0);
    assert.equal(returns.length, 1); assert.equal(returns[0].data.amount, 1);
    const other = await checked(s, 4); expectStatus(await appeal(other.id), 201);
  });
  it('R16 возвращённая квота не разрешает повторную апелляцию того же задания', async () => {
    const s = await setup(), c = await checked(s), a = await claimedAppeal(c.id);
    expectStatus(await resolve(a.id), 200);
    expectError(await appeal(c.id), 409, 'APPEAL_ALREADY_USED');
  });
  it('R17 отклонение апелляции не меняет вердикт и не возвращает квоту', async () => {
    const s = await setup(), c = await checked(s), a = await claimedAppeal(c.id);
    const result = expectStatus(await resolve(a.id, { outcome: 'upheld' }), 200);
    assert.equal(result.check.verdict, 'incorrect'); assert.equal((await quota(s)).used, 1);
    assert.equal((await events(s.game.id, 'AppealUsageReturned')).length, 0);
  });
  it('R18 одного признания без исправленных данных недостаточно для возврата', async () => {
    const s = await setup(), c = await checked(s), a = await claimedAppeal(c.id);
    expectError(await api('POST', `/appeals/${a.id}/resolve`, { role: 'checker', body: { outcome: 'jury-error-corrected', reason: 'Признаём ошибку' } }), 422, 'CORRECTION_REQUIRED');
    assert.equal((await quota(s)).used, 1);
  });
  it('R19 задержанная доставка и проверка ранее принятого ответа разрешены после срока', async () => {
    const s = await setup({ submissionOpen: false, appealOpen: false, finishExpired: true });
    assert.equal((await checked(s)).status, 'checked');
  });
  it('R20 уже поданную апелляцию можно рассмотреть после дедлайна', async () => {
    const s = await setup(), c = await checked(s), a = await claimedAppeal(c.id);
    await expire(s); assert.equal(expectStatus(await resolve(a.id), 200).status, 'resolved');
  });
  it('R21 пустая очередь без manifest не означает завершение', async () => {
    const s = await setup({ submissionOpen: false, appealOpen: false, finishExpired: true });
    const result = await completion(s); assert.equal(result.complete, false); assert.equal(result.manifestReceived, false);
  });
  it('R22 недоставленный ответ из manifest удерживает завершение', async () => {
    const s = await setup({ submissionOpen: false, appealOpen: false, finishExpired: true });
    expectStatus(await manifest(s, [s.receipts[0].id]), 200);
    let state = await completion(s); assert.equal(state.complete, false); assert.deepEqual(state.missingReceiptIds, [s.receipts[0].id]);
    await checked(s);
    state = await eventually(() => completion(s), r => r.complete);
    assert.deepEqual(state.missingReceiptIds, []);
  });
  it('R23 непроверенный принятый ответ удерживает завершение', async () => {
    const s = await setup({ submissionOpen: false, appealOpen: false, finishExpired: true }), c = expectStatus(await ingest(s), 201);
    expectStatus(await manifest(s, [c.receiptId]), 200);
    const result = await completion(s); assert.equal(result.complete, false); assert.ok(result.pendingCheckIds.includes(c.id));
  });
  it('R24 принятая апелляция удерживает завершение до решения', async () => {
    const s = await setup(), c = await checked(s), a = await claimedAppeal(c.id); await expire(s);
    expectStatus(await manifest(s, [c.receiptId]), 200);
    const result = await completion(s); assert.equal(result.complete, false); assert.ok(result.pendingAppealIds.includes(a.id));
    expectStatus(await resolve(a.id), 200);
    await eventually(() => completion(s), r => r.complete);
    const completed = await eventually(() => events(s.game.id, 'AllChecksCompleted'), e => e.length > 0);
    assert.deepEqual(completed[0].data.receiptIds, [c.receiptId]);
  });
  it('R25 открытое окно апелляций не даёт преждевременно подтвердить завершение', async () => {
    const s = await setup({ submissionOpen: false, appealOpen: true }), c = await checked(s);
    expectStatus(await manifest(s, [c.receiptId]), 200); assert.equal((await completion(s)).complete, false);
  });
  it('R26 старый manifest не закрывает игру после разрешения поздней сдачи', async () => {
    const s = await setup({ submissionOpen: false, appealOpen: false }), c = await checked(s);
    const oldVersion = (await currentGame(s)).admissionVersion;
    expectStatus(await manifest(s, [c.receiptId], oldVersion), 200);
    await eventually(() => completion(s), r => r.complete);
    await fixture('PATCH', `/__test/scenarios/${s.game.id}`, { lateSubmissionAllowed: true });
    expectStatus(await manifest(s, [c.receiptId], oldVersion), 200);
    const state = await completion(s);
    assert.equal(state.complete, false); assert.equal(state.acceptingSubmissions, true);
    assert.ok(state.admissionVersion > oldVersion);
    assert.equal(expectStatus(await api('GET', `/checks/${c.id}`), 200).verdict, 'incorrect');
  });
  it('R27 административная коррекция доступна в домино и не возвращает квоту', async () => {
    const s = await setup({ system: 'domino' }), c = await checked(s);
    const changed = expectStatus(await api('POST', `/checks/${c.id}/corrections`, { body: { verdict: 'correct', reason: 'Исправление администратором' } }), 200);
    assert.equal(changed.originalVerdict, 'incorrect'); assert.equal(changed.verdict, 'correct'); assert.equal(changed.history.length, 2);
    assert.equal((await quota(s)).used, 0);
    const corrected = await eventually(() => events(s.game.id, 'VerdictCorrected'), e => e.length > 0);
    assert.equal(corrected[0].data.revision, 2);
  });
  it('R28 роли защищают внутренний приём, проверку и апелляции', async () => {
    const s = await setup();
    expectError(await ingest(s, 0, { role: 'observer' }), 403, 'FORBIDDEN');
    const c = expectStatus(await ingest(s), 201);
    expectError(await claim(c.id, 'observer'), 403, 'FORBIDDEN');
    expectError(await api('GET', `/checks/${c.id}`, { role: null }), 401, 'UNAUTHENTICATED');
    expectError(await api('GET', `/checks/${c.id}`, { role: 'organizer' }), 403, 'FORBIDDEN');
    expectStatus(await claim(c.id, 'multi'), 200);
    expectStatus(await verdict(s, c.id, {}, { role: 'multi' }), 200);
    expectError(await appeal(c.id, { role: 'checker' }), 403, 'FORBIDDEN');
    expectStatus(await appeal(c.id, { role: 'multi' }), 201);
  });
  it('R29 освобождённое дело доступно другому проверяющему', async () => {
    const s = await setup(), c = expectStatus(await ingest(s), 201); expectStatus(await claim(c.id), 200);
    expectStatus(await api('POST', `/checks/${c.id}/release`, { role: 'checker' }), 200);
    assert.equal(expectStatus(await claim(c.id, 'checker-2'), 200).assignedTo, 'test-checker-2');
  });
  it('R30 события отражают ожидание апелляции и результат её рассмотрения', async () => {
    const s = await setup(), c = await checked(s), a = await claimedAppeal(c.id);
    const opened = await eventually(() => events(s.game.id, 'AppealOpened'), e => e.length > 0);
    assert.equal(opened[0].data.appealId, a.id); assert.equal(opened[0].data.taskId, s.taskIds[0]);
    expectStatus(await resolve(a.id, { outcome: 'upheld' }), 200);
    const resolved = await eventually(() => events(s.game.id, 'AppealResolved'), e => e.length > 0);
    assert.equal(resolved[0].data.outcome, 'upheld'); assert.equal(resolved[0].data.checkId, c.id);
  });
  it('R31 нулевой лимит запрещает подачу, изменяет лимит только администратор', async () => {
    const s = await setup({ quota: 0 }), c = await checked(s);
    expectError(await appeal(c.id), 409, 'APPEAL_QUOTA_EXHAUSTED');
    expectError(await api('PUT', `/appeal-quotas/${s.game.id}`, { role: 'observer', body: { limit: 100 } }), 403, 'FORBIDDEN');
  });
  it('R32 повтор сохранения вердикта не добавляет историю и revision', async () => {
    const s = await setup(), c = expectStatus(await ingest(s), 201), idempotencyKey = key(); expectStatus(await claim(c.id), 200);
    const first = expectStatus(await verdict(s, c.id, {}, { idempotencyKey }), 200);
    const repeated = expectStatus(await verdict(s, c.id, {}, { idempotencyKey }), 200);
    assert.equal(repeated.revision, first.revision); assert.equal(repeated.history.length, 1);
    await eventually(() => events(s.game.id, 'VerdictIssued'), e => e.length > 0);
    assert.equal((await events(s.game.id, 'VerdictIssued')).length, 1);
  });
  it('R33 в работе сразу есть снимок, номер задачи и серверный эталон', async () => {
    const s = await setup(), c = expectStatus(await ingest(s, 2), 201);
    const assigned = expectStatus(await claim(c.id), 200);
    const task = expectStatus(await fixture('GET', `/tasks/${s.receipts[2].taskId}`), 200);
    assert.equal(assigned.imageRef, s.receipts[2].imageRef);
    assert.equal(assigned.taskId, s.receipts[2].taskId); assert.equal(assigned.taskNumber, s.receipts[2].taskNumber);
    assert.equal(assigned.referenceAnswer, task.referenceAnswer); assert.equal(assigned.referenceAnswer, '5');
  });
  it('R34 нечитаемый текст оценивается кнопкой «неверно», без особого состояния', async () => {
    const s = await setup(), c = expectStatus(await ingest(s), 201); expectStatus(await claim(c.id), 200);
    const result = expectStatus(await verdict(s, c.id), 200);
    assert.equal(result.verdict, 'incorrect'); assert.equal(result.status, 'checked');
  });
  it('R35 новый тип игры использует возможности профиля без изменения ядра', async () => {
    for (const appealsAllowed of [true, false]) {
      const s = await setup({ system: 'new-olympiad-v4', appealsAllowed, modifiersAllowed: false });
      const c = await checked(s);
      if (appealsAllowed) expectStatus(await appeal(c.id), 201);
      else expectError(await appeal(c.id), 409, 'APPEAL_NOT_ALLOWED');
    }
  });
  it('R36 поздняя сдача не сбрасывает квоту и запрет повторной апелляции', async () => {
    const s = await setup({ quota: 2 }), c = await checked(s), a = await claimedAppeal(c.id);
    expectStatus(await resolve(a.id, { outcome: 'upheld' }), 200);
    await expire(s);
    await fixture('PATCH', `/__test/scenarios/${s.game.id}`, { lateSubmissionAllowed: true, appealOpen: true });
    assert.equal((await quota(s)).used, 1);
    expectError(await appeal(c.id), 409, 'APPEAL_ALREADY_USED');
    assert.equal((await completion(s)).complete, false);
  });
  it('R37 новый итог приёма включает старые и поздние ответы одной игры', async () => {
    const s = await setup({ submissionOpen: false, appealOpen: false }), first = await checked(s);
    expectStatus(await manifest(s, [first.receiptId]), 200);
    await eventually(() => completion(s), r => r.complete);
    await fixture('PATCH', `/__test/scenarios/${s.game.id}`, { lateSubmissionAllowed: true });
    const e = receiptEvent(s, 2); e.data.admissionVersion = (await currentGame(s)).admissionVersion;
    e.data.acceptedAt = new Date().toISOString();
    const second = expectStatus(await ingest(s, 2, { body: e }), 201);
    await fixture('PATCH', `/__test/scenarios/${s.game.id}`, { lateSubmissionAllowed: false });
    expectStatus(await manifest(s, [first.receiptId, second.receiptId]), 200);
    assert.equal((await completion(s)).complete, false);
    expectStatus(await claim(second.id), 200); expectStatus(await verdict(s, second.id), 200);
    await eventually(() => completion(s), r => r.complete);
    const list = expectStatus(await api('GET', `/checks?gameId=${s.game.id}`, { role: 'checker' }), 200).items;
    assert.equal(list.length, 2); assert.ok(list.some(c => c.id === first.id));
  });
  it('R38 неизменённый вердикт не считается исправлением ошибки жюри', async () => {
    const s = await setup(), c = await checked(s), a = await claimedAppeal(c.id);
    expectError(await resolve(a.id, { verdict: 'incorrect' }), 422, 'VERDICT_UNCHANGED');
    assert.equal((await quota(s)).used, 1);
  });

});
