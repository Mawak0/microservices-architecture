import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL, fileURLToPath } from 'node:url';
import Ajv2020 from 'ajv/dist/2020.js';
import addFormats from 'ajv-formats';

const directory = path.resolve('contracts');
const uri = name => pathToFileURL(path.join(directory, name)).href;
const escape = value => value.replace(/~/g, '~0').replace(/\//g, '~1');
let registry;
function getRegistry() {
  if (registry) return registry;
  const ajv = new Ajv2020({ strict: false, allErrors: true }); addFormats(ajv);
  const docs = new Map();
  for (const name of fs.readdirSync(directory).filter(n => n.endsWith('.json'))) {
    const id = uri(name), doc = JSON.parse(fs.readFileSync(fileURLToPath(id), 'utf8'));
    docs.set(id, doc); ajv.addSchema(doc, id);
  }
  registry = { ajv, docs, compiled: new Map() }; return registry;
}
function validator(ref) {
  const r = getRegistry();
  if (!r.compiled.has(ref)) r.compiled.set(ref, r.ajv.compile({ $ref: ref }));
  return r.compiled.get(ref);
}
function check(ref, data) {
  const validate = validator(ref);
  assert.ok(validate(data), `${ref}: ${JSON.stringify(validate.errors)}`);
}
function resolve(ref) {
  const [file, fragment = ''] = ref.split('#');
  let value = getRegistry().docs.get(file);
  for (const part of fragment.split('/').slice(1)) value = value?.[part.replace(/~1/g, '/').replace(/~0/g, '~')];
  assert.ok(value, `Не найдена ссылка ${ref}`);
  return value.$ref ? resolve(new URL(value.$ref, file).href) : { value, ref };
}
export function validateResponse(method, requestPath, response) {
  const api = getRegistry().docs.get(uri('openapi.json'));
  const pathname = requestPath.split('?')[0];
  const route = Object.keys(api.paths).find(route => {
    const a = route.split('/'), b = pathname.split('/');
    return a.length === b.length && a.every((segment, i) => /^\{.+\}$/.test(segment) || segment === b[i]);
  });
  assert.ok(route, `Маршрут ${method} ${requestPath} отсутствует в контракте`);
  const verb = method.toLowerCase(), op = api.paths[route][verb];
  assert.ok(op, `Операция ${method} ${route} отсутствует в контракте`);
  const status = op.responses[String(response.status)] ? String(response.status) : 'default';
  assert.ok(op.responses[status], `Неописанный HTTP ${response.status}`);
  const target = resolve(`${uri('openapi.json')}#/paths/${escape(route)}/${verb}/responses/${status}`);
  const content = target.value.content?.['application/json'];
  if (content?.schema) {
    assert.match(response.headers.get('content-type') || '', /application\/json/);
    check(`${target.ref}/content/application~1json/schema`, response.data);
  }
}
export const validateEvent = event => check(uri('events.schema.json'), event);

export function compileAllSchemas() {
  const r = getRegistry(); let count = 0;
  for (const [id, document] of r.docs) {
    if (document.$schema) { validator(id); count++; }
    if (!document.openapi) continue;
    function visit(value, pointer = '') {
      if (!value || typeof value !== 'object') return;
      for (const [key, child] of Object.entries(value)) {
        const next = `${pointer}/${escape(key)}`;
        if (key === 'schema' || pointer === '/components/schemas') { validator(`${id}#${next}`); count++; }
        else visit(child, next);
      }
    }
    visit(document);
  }
  return count;
}
