import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';

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
