import { describe, it, before } from 'node:test';
import assert from 'node:assert/strict';
import { api, fixture, scenario, requireService, expectStatus, expectError, key, eventually, events } from '../test-support/client.mjs';

const issue = (s, count = 2, extra = {}, options = {}) => api('POST', '/blanks', {
  body: { gameId: s.game.id, teamId: s.teamId, taskId: s.taskIds[0], count, ...extra }, ...options
});
const prepare = s => api('POST', '/kits', { body: { gameId: s.game.id, teamId: s.teamId } });
async function readyJob(id) {
  const r = await eventually(() => api('GET', `/print-jobs/${id}`, { role: 'observer' }),
    r => r.status === 200 && ['completed', 'failed'].includes(r.data.status));
  assert.equal(r.data.status, 'completed', JSON.stringify(r.data)); return r.data;
}

describe('ТЗ: бланки и печатные материалы', { concurrency: false }, () => {
  before(requireService);
  it('M01 комплект использует настройки количества', async () => {
    const s = await scenario({ answerBlankCount: 7, taskCardCount: 2 }), kit = expectStatus(await prepare(s), 201);
    assert.equal(kit.gameId, s.game.id); assert.equal(kit.teamId, s.teamId);
    assert.equal(kit.answerBlankCount, s.game.materialSettings.answerBlankCount);
    assert.equal(kit.taskCardCount, s.game.materialSettings.taskCardCount);
    assert.deepEqual(expectStatus(await api('GET', `/kits/${kit.id}`), 200), kit);
  });
  it('M02 команда должна быть допущена', async () => {
    expectError(await prepare(await scenario({ admitted: false })), 409, 'TEAM_NOT_ADMITTED');
  });
  it('M03 выпуск новых и дополнительных бланков сохраняет принадлежность и уникальность', async () => {
    const s = await scenario();
    const a = expectStatus(await issue(s, 3), 201).blanks;
    const b = expectStatus(await issue(s, 2), 201).blanks;
    assert.equal(a.length, 3); assert.equal(b.length, 2);
    assert.equal(new Set([...a, ...b].map(b => b.id)).size, 5);
    assert.equal(new Set([...a, ...b].map(b => b.qrToken)).size, 5);
    for (const blank of [...a, ...b]) {
      assert.equal(blank.gameId, s.game.id); assert.equal(blank.teamId, s.teamId);
      const verified = expectStatus(await api('POST', '/blank-verifications', { body: { qrToken: blank.qrToken }, role: 'system' }), 200);
      assert.equal(verified.id, blank.id); assert.equal(verified.number, blank.number);
      assert.equal(blank.taskId, s.taskIds[0]); assert.equal(blank.taskNumber, '1');
      assert.equal(verified.taskId, blank.taskId); assert.equal(verified.taskNumber, blank.taskNumber);
    }
  });
  it('M04 бонусные бланки заранее связаны с задачей и сохраняют модификаторы', async () => {
    const s = await scenario();
    for (const modifier of [{ kind: 'add', value: 3 }, { kind: 'multiply', value: 2 }]) {
      const b = expectStatus(await issue(s, 1, { modifier }), 201).blanks[0];
      assert.deepEqual(b.modifier, modifier);
      assert.deepEqual(expectStatus(await api('POST', '/blank-verifications', { body: { qrToken: b.qrToken } }), 200).modifier, modifier);
    }
  });
  it('M05 другие игры не допускают модификатор', async () => {
    for (const system of ['domino', 'tic-tac-toe']) {
      const s = await scenario({ system });
      expectError(await issue(s, 1, { modifier: { kind: 'add', value: 3 } }), 422, 'MODIFIER_NOT_ALLOWED');
      assert.equal(expectStatus(await issue(s, 1), 201).blanks[0].modifier, null);
    }
  });
  it('M06 выпуск доступен только администратору', async () => {
    const s = await scenario();
    for (const role of ['observer', 'checker', 'organizer']) expectError(await issue(s, 1, {}, { role }), 403, 'FORBIDDEN');
  });
  it('M07 поддельный QR не подтверждается', async () => {
    const s = await scenario(), b = expectStatus(await issue(s, 1), 201).blanks[0];
    for (const qrToken of ['unknown-qr', `${b.qrToken}tampered`])
      expectError(await api('POST', '/blank-verifications', { body: { qrToken } }), 422, 'INVALID_QR');
  });
  it('M08 повтор с тем же ключом не выпускает новые ID', async () => {
    const s = await scenario(), idempotencyKey = key();
    const a = expectStatus(await issue(s, 2, {}, { idempotencyKey }), 201);
    const b = expectStatus(await issue(s, 2, {}, { idempotencyKey }), [200, 201]);
    assert.deepEqual(b, a);
    expectError(await issue(s, 3, {}, { idempotencyKey }), 409, 'IDEMPOTENCY_KEY_REUSED');
  });
  it('M09 наблюдатель печатает полный комплект и часть, повторная печать не меняет QR', async () => {
    const s = await scenario(); await prepare(s);
    const blanks = expectStatus(await issue(s, 3), 201).blanks;
    for (const selected of [blanks, blanks.slice(0, 1), blanks.slice(0, 1)]) {
      const job = expectStatus(await api('POST', '/print-jobs', { role: 'observer', body: {
        gameId: s.game.id, teamId: s.teamId, blankIds: selected.map(b => b.id), taskIds: []
      } }), 202);
      const done = await readyJob(job.id);
      assert.deepEqual(done.manifest.map(m => m.id).sort(), selected.map(b => b.id).sort());
    }
    for (const b of blanks) assert.equal(expectStatus(await api('GET', `/blanks/${b.id}`), 200).qrToken, b.qrToken);
  });
  it('M10 единый PDF включает условия и бланки, manifest не раскрывает эталоны', async () => {
    const s = await scenario(); await prepare(s);
    const blanks = expectStatus(await issue(s, 2), 201).blanks;
    const job = expectStatus(await api('POST', '/print-jobs', { body: {
      gameId: s.game.id, teamId: s.teamId, blankIds: blanks.map(b => b.id), taskIds: s.taskIds
    } }), 202);
    const done = await readyJob(job.id);
    assert.deepEqual(done.manifest.filter(m => m.kind === 'answer-blank').map(m => m.id).sort(), blanks.map(b => b.id).sort());
    assert.deepEqual(done.manifest.filter(m => m.kind === 'task-statement').map(m => m.id).sort(), [...s.taskIds].sort());
    for (const entry of done.manifest) assert.deepEqual(Object.keys(entry).sort(), ['id', 'kind']);
    const pdf = await api('GET', `/print-jobs/${job.id}/artifact`); expectStatus(pdf, 200);
    assert.match(pdf.headers.get('content-type'), /application\/pdf/);
    assert.equal(pdf.data.subarray(0, 5).toString(), '%PDF-');
  });
  it('M11 нельзя подмешать материалы другой команды или игры', async () => {
    const s = await scenario(), other = await scenario(); await prepare(s);
    for (const source of [{ ...s, teamId: s.otherTeamId }, other]) {
      const b = expectStatus(await issue(source, 1), 201).blanks[0];
      expectError(await api('POST', '/print-jobs', { body: { gameId: s.game.id, teamId: s.teamId, blankIds: [b.id], taskIds: [] } }), 422, 'MATERIAL_SCOPE_MISMATCH');
    }
  });
  it('M12 закрытые операции не доступны гостю и посторонним ролям', async () => {
    const s = await scenario(), body = { gameId: s.game.id, teamId: s.teamId, blankIds: [], taskIds: s.taskIds };
    expectError(await api('POST', '/print-jobs', { role: null, body }), 401, 'UNAUTHENTICATED');
    for (const role of ['organizer', 'checker']) expectError(await api('POST', '/print-jobs', { role, body }), 403, 'FORBIDDEN');
  });
  it('M13 параллельный выпуск не создаёт одинаковые ID и QR', async () => {
    const s = await scenario(); const results = await Promise.all(Array.from({ length: 5 }, () => issue(s, 2)));
    const all = results.flatMap(r => expectStatus(r, 201).blanks);
    assert.equal(all.length, 10); assert.equal(new Set(all.map(b => b.id)).size, 10); assert.equal(new Set(all.map(b => b.qrToken)).size, 10);
  });
  it('M14 комплект домино поддерживает общие карточки', async () => {
    const s = await scenario({ system: 'domino' });
    assert.equal(expectStatus(await prepare(s), 201).taskCardsShared, true);
  });
  it('M15 событие выпуска содержит зарегистрированные ID', async () => {
    const s = await scenario(), result = expectStatus(await issue(s, 2), 201);
    const list = await eventually(() => events(s.game.id, 'BlanksIssued'), e => e.length > 0);
    assert.deepEqual(list.flatMap(e => e.data.blankIds).sort(), result.blanks.map(b => b.id).sort());
  });
  it('M16 пустая печать и неправильное количество отклоняются', async () => {
    const s = await scenario();
    for (const count of [0, -1, 1.5]) expectError(await issue(s, count), 422, 'VALIDATION_ERROR');
    expectError(await api('POST', '/print-jobs', { body: { gameId: s.game.id, teamId: s.teamId, blankIds: [], taskIds: [] } }), 422, 'VALIDATION_ERROR');
  });
  it('M17 сочетание ролей сохраняет возможности наблюдателя', async () => {
    const s = await scenario(); await prepare(s);
    const b = expectStatus(await issue(s, 1), 201).blanks[0];
    expectStatus(await api('POST', '/print-jobs', { role: 'multi', body: { gameId: s.game.id, teamId: s.teamId, blankIds: [b.id], taskIds: [] } }), 202);
  });
  it('M18 администратор формирует единый PDF всей игры для разных команд', async () => {
    const s = await scenario(), other = { ...s, teamId: s.otherTeamId };
    await prepare(s); await prepare(other);
    const blanks = [...expectStatus(await issue(s, 2), 201).blanks, ...expectStatus(await issue(other, 2), 201).blanks];
    const body = { gameId: s.game.id, blankIds: blanks.map(b => b.id), taskIds: s.taskIds };
    const job = expectStatus(await api('POST', '/print-jobs', { body }), 202);
    const done = await readyJob(job.id); assert.equal(done.teamId, null);
    assert.deepEqual(done.manifest.filter(m => m.kind === 'answer-blank').map(m => m.id).sort(), blanks.map(b => b.id).sort());
  });
  it('M19 задача другой игры не попадает в печатные материалы', async () => {
    const s = await scenario(), other = await scenario(); await prepare(s);
    expectError(await api('POST', '/print-jobs', { body: { gameId: s.game.id, teamId: s.teamId, blankIds: [], taskIds: other.taskIds } }), 422, 'MATERIAL_SCOPE_MISMATCH');
  });
  it('M20 при выпуске обязательна задача, назначенная этой игре', async () => {
    const s = await scenario(), other = await scenario();
    expectError(await api('POST', '/blanks', { body: { gameId: s.game.id, teamId: s.teamId, count: 1 } }), 422, 'VALIDATION_ERROR');
    expectError(await issue(s, 1, { taskId: other.taskIds[0] }), 422, 'TASK_NOT_IN_GAME');
    const b = expectStatus(await issue(s, 1, { taskId: s.taskIds[1] }), 201).blanks[0];
    assert.equal(b.taskId, s.taskIds[1]); assert.equal(b.taskNumber, '2');
    const verified = expectStatus(await api('POST', '/blank-verifications', { body: { qrToken: b.qrToken } }), 200);
    assert.equal(verified.taskId, b.taskId); assert.equal(verified.taskNumber, '2');
  });
  it('M21 новый тип игры использует шаблон и возможности из настроек', async () => {
    const modifier = { kind: 'add', value: 3 };
    for (const modifiersAllowed of [true, false]) {
      const s = await scenario({ system: 'new-olympiad-v4', modifiersAllowed,
        templateKey: 'custom/answer-sheet-v2', templateHeading: 'Новая игра' });
      const kit = expectStatus(await prepare(s), 201);
      assert.equal(kit.templateKey, 'custom/answer-sheet-v2');
      if (!modifiersAllowed) expectError(await issue(s, 1, { modifier }), 422, 'MODIFIER_NOT_ALLOWED');
      const b = expectStatus(await issue(s, 1, modifiersAllowed ? { modifier } : {}), 201).blanks[0];
      const job = expectStatus(await api('POST', '/print-jobs', { body: { gameId: s.game.id,
        teamId: s.teamId, blankIds: [b.id], taskIds: s.taskIds } }), 202);
      const done = await readyJob(job.id);
      assert.equal(done.templateKey, 'custom/answer-sheet-v2');
      assert.equal(done.templateParameters.heading, 'Новая игра');
    }
  });
  it('M22 разные шаблоны одной системы не меняют уже созданное задание печати', async () => {
    const s = await scenario({ templateKey: 'bonuses/layout-a' }); await prepare(s);
    const b = expectStatus(await issue(s, 1), 201).blanks[0];
    const body = { gameId: s.game.id, teamId: s.teamId, blankIds: [b.id], taskIds: [] };
    const first = expectStatus(await api('POST', '/print-jobs', { body }), 202);
    const doneFirst = await readyJob(first.id);
    await fixture('PATCH', `/__test/scenarios/${s.game.id}`, { templateKey: 'bonuses/layout-b', templateHeading: 'Другой макет' });
    const second = expectStatus(await api('POST', '/print-jobs', { body }), 202);
    assert.equal((await readyJob(second.id)).templateKey, 'bonuses/layout-b');
    assert.equal(expectStatus(await api('GET', `/print-jobs/${first.id}`), 200).templateKey, doneFirst.templateKey);
    assert.equal(doneFirst.templateKey, 'bonuses/layout-a');
  });
  it('M23 публичная модель бланка не содержит эталонного ответа', async () => {
    const s = await scenario(), b = expectStatus(await issue(s, 1), 201).blanks[0];
    const fetched = expectStatus(await api('GET', `/blanks/${b.id}`, { role: 'observer' }), 200);
    const verified = expectStatus(await api('POST', '/blank-verifications', { body: { qrToken: b.qrToken }, role: 'observer' }), 200);
    assert.equal(b.referenceAnswer, undefined); assert.equal(fetched.referenceAnswer, undefined);
    assert.equal(verified.referenceAnswer, undefined);
  });

  it('M24 новая задача с исправленным эталоном не меняет выданные бланки', async () => {
    const s = await scenario(); await prepare(s);
    const b = expectStatus(await issue(s, 1), 201).blanks[0];
    const alternative = expectStatus(await fixture('POST', '/__test/alternative-tasks', {
      taskId: b.taskId, referenceAnswer: 'новый эталон'
    }), 201);
    assert.notEqual(alternative.id, b.taskId);
    expectError(await issue(s, 1, { taskId: alternative.id }), 422, 'TASK_NOT_IN_GAME');
    expectError(await api('POST', '/print-jobs', { body: {
      gameId: s.game.id, teamId: s.teamId, blankIds: [], taskIds: [alternative.id]
    } }), 422, 'MATERIAL_SCOPE_MISMATCH');
    const job = expectStatus(await api('POST', '/print-jobs', { role: 'observer', body: {
      gameId: s.game.id, teamId: s.teamId, blankIds: [b.id], taskIds: [b.taskId]
    } }), 202);
    await readyJob(job.id);
    const fetched = expectStatus(await api('GET', `/blanks/${b.id}`), 200);
    const verified = expectStatus(await api('POST', '/blank-verifications', { body: { qrToken: b.qrToken } }), 200);
    assert.equal(fetched.taskId, b.taskId); assert.equal(fetched.qrToken, b.qrToken);
    assert.equal(verified.taskId, b.taskId); assert.equal(verified.taskNumber, b.taskNumber);
  });

});
