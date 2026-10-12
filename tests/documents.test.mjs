import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { compileAllSchemas, validateEvent, validateResponse } from '../test-support/schemas.mjs';
import { spawnSync } from 'node:child_process';

const dir = path.resolve('contracts');
const load = file => JSON.parse(fs.readFileSync(file, 'utf8'));
const files = fs.readdirSync(dir).filter(n => n.endsWith('.json')).map(n => path.join(dir, n));
test('Все JSON-документы читаются; все локальные ссылки разрешаются', () => {
  let references = 0;
  function visit(node, file) {
    if (!node || typeof node !== 'object') return;
    if (node.$ref) {
      assert.ok(!/^https?:/.test(node.$ref), 'Контракты должны проверяться офлайн');
      const [relative, fragment] = node.$ref.split('#');
      const target = path.resolve(path.dirname(file), relative || path.basename(file));
      assert.ok(target.startsWith(dir + path.sep));
      let value = load(target);
      for (const part of (fragment || '').split('/').slice(1)) value = value?.[part.replace(/~1/g, '/').replace(/~0/g, '~')];
      assert.notEqual(value, undefined, `Не найдена ссылка ${node.$ref}`); references++;
    }
    Object.values(node).forEach(v => visit(v, file));
  }
  for (const file of files) visit(load(file), file);
  assert.ok(references > 0);
});
test('HTTP-контракт содержит операции, ответы и авторизацию', () => {
  const api = load(path.join(dir, 'openapi.json'));
  assert.equal(api.openapi, '3.1.0'); assert.ok(api.info.title); assert.ok(api.security.length);
  const ids = new Set(); let count = 0;
  for (const [route, item] of Object.entries(api.paths)) {
    assert.ok(route.startsWith('/'));
    for (const method of ['get', 'post', 'put', 'patch', 'delete']) {
      const op = item[method]; if (!op) continue;
      assert.ok(op.operationId); assert.ok(!ids.has(op.operationId)); ids.add(op.operationId);
      assert.ok(Object.keys(op.responses).some(s => /^2/.test(s)));
      for (const r of Object.values(op.responses)) assert.ok(r.description || r.$ref);
      for (const match of route.matchAll(/\{([^}]+)\}/g)) {
        assert.ok([...(item.parameters || []), ...(op.parameters || [])].some(p => p.in === 'path' && p.name === match[1] && p.required));
      }
      if (['post', 'put', 'patch'].includes(method)) assert.ok(op.requestBody || op.description);
      count++;
    }
  }
  assert.ok(count >= 4);
});
test('События имеют JSON Schema и перечисленный публичный тип', () => {
  const schema = load(path.join(dir, 'events.schema.json'));
  assert.equal(schema.$schema, 'https://json-schema.org/draft/2020-12/schema');
  assert.ok(schema.allOf?.length); assert.ok(schema.properties.type.enum.length);
});
test('Ajv компилирует все схемы запросов, ответов и событий с локальными ссылками', () => {
  assert.ok(compileAllSchemas() >= 5);
});
test('Валидатор не пропускает неполное событие и повреждённый ответ API', () => {
  assert.throws(() => validateEvent({ type: 'unknown', data: {} }));
  const api = load(path.join(dir, 'openapi.json'));
  const [route, item] = Object.entries(api.paths).find(([, item]) => item.post);
  const method = 'POST';
  assert.throws(() => validateResponse(method, route, {
    status: 422, data: {}, headers: new Headers({ 'content-type': 'application/json' })
  }));
  validateResponse(method, route, { status: 422, data: { code: 'VALIDATION_ERROR', message: 'Некорректные данные' },
    headers: new Headers({ 'content-type': 'application/json' }) });
  const type = load(path.join(dir, 'events.schema.json')).properties.type.enum[0];
  validateEvent({ eventId: 'event-1', type, aggregateId: 'aggregate-1', occurredAt: new Date().toISOString(), data: {
    gameId: 'game-1', cycleId: 'cycle-1', teamId: 'team-1', kitId: 'kit-1', receiptId: 'receipt-1', blankId: 'blank-1',
    imageRef: 'fixture://image-1', acceptedAt: new Date().toISOString(), checkId: 'check-1', taskId: 'task-1', verdict: 'correct', revision: 1
  } });
});
test('Каждому требованию трассировки соответствует исполняемый сценарий', () => {
  const table = fs.readFileSync(path.join(dir, 'requirements.md'), 'utf8');
  const suite = fs.readFileSync('tests/acceptance.test.mjs', 'utf8');
  const required = [...table.matchAll(/\|\s([MRI]\d+)\s\|/g)].map(m => m[1]);
  const implemented = [...suite.matchAll(/it\('([MRI]\d+) /g)].map(m => m[1]);
  assert.ok(required.length > 0); assert.equal(new Set(implemented).size, implemented.length);
  assert.deepEqual([...required].sort(), [...implemented].sort());
});
test('Приёмочные тесты без сервиса завершаются ошибкой, а не зелёным skip', () => {
  const childEnv = { ...process.env, SERVICE_BASE_URL: '', CONTRACT_TEST_ALLOW_WRITES: '' };
  // Вложенный runner должен стартовать как отдельный процесс, не как worker родителя.
  for (const name of Object.keys(childEnv)) if (name.startsWith('NODE_TEST_')) delete childEnv[name];
  const result = spawnSync(process.execPath, ['--test', 'tests/acceptance.test.mjs'], {
    env: childEnv, encoding: 'utf8', timeout: 10000
  });
  assert.equal(result.status, 1); assert.match(result.stdout + result.stderr, /SERVICE_BASE_URL/);
});
