import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { validateResponse, validateEvent } from './schemas.mjs';

export const token = role => `test-${role}`;
export const key = () => randomUUID();
export const fixtureBase = process.env.FIXTURE_BASE_URL || 'http://127.0.0.1:19100';
export function requireService() {
  assert.ok(process.env.SERVICE_BASE_URL, 'Нет реализации: задайте SERVICE_BASE_URL тестового сервиса');
  assert.equal(process.env.CONTRACT_TEST_ALLOW_WRITES, '1', 'Для записи в тестовый сервис задайте CONTRACT_TEST_ALLOW_WRITES=1');
  const url = new URL(process.env.SERVICE_BASE_URL);
  assert.ok(['http:', 'https:'].includes(url.protocol)); return url.href.replace(/\/$/, '');
}
export async function request(base, method, path, { body, role = 'admin', idempotencyKey = key() } = {}) {
  const headers = {};
  if (role !== null) headers.authorization = `Bearer ${token(role)}`;
  if (body !== undefined) headers['content-type'] = 'application/json';
  if (['POST', 'PUT'].includes(method)) headers['idempotency-key'] = idempotencyKey;
  const res = await fetch(`${base}${path}`, { method, headers, body: body === undefined ? undefined : JSON.stringify(body),
    signal: AbortSignal.timeout(Number(process.env.TEST_TIMEOUT_MS || 10000)) });
  const raw = Buffer.from(await res.arrayBuffer());
  const data = res.headers.get('content-type')?.includes('json') && raw.length ? JSON.parse(raw) : raw;
  return { status: res.status, data, headers: res.headers };
}
export async function api(method, path, options) {
  const response = await request(requireService(), method, path, options);
  validateResponse(method, path, response);
  return response;
}
export const fixture = (method, path, body) => request(fixtureBase, method, path, { body, role: null });
export async function scenario(options = {}) {
  const r = await fixture('POST', '/__test/scenarios', options); assert.equal(r.status, 201); return r.data;
}
export function expectStatus(r, status) {
  assert.ok([status].flat().includes(r.status), `Ожидали HTTP ${status}, получили ${r.status}: ${JSON.stringify(r.data)}`);
  return r.data;
}
export function expectError(r, status, code) {
  expectStatus(r, status); assert.equal(r.data.code, code); assert.equal(typeof r.data.message, 'string');
}
export async function eventually(operation, predicate, message = 'Асинхронное условие не выполнено') {
  const end = Date.now() + Number(process.env.TEST_TIMEOUT_MS || 10000);
  let value;
  do { value = await operation(); if (predicate(value)) return value; await new Promise(r => setTimeout(r, 100)); } while (Date.now() < end);
  assert.fail(`${message}: ${JSON.stringify(value)}`);
}
export async function events(gameId, type) {
  const r = await fixture('GET', `/__test/events?gameId=${gameId}`);
  expectStatus(r, 200);
  for (const event of r.data) validateEvent(event);
  return r.data.filter(e => !type || e.type === type);
}
// Минимальная корректная PNG для транспортных тестов, не образец читаемого бланка.
export const pngBase64 = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4////fwAJ+wP9KobjigAAAABJRU5ErkJggg==';
