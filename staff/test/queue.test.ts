import { describe, expect, it, vi } from 'vitest';
import { IDBFactory } from 'fake-indexeddb';
import { ApiError, json, StaffApi } from '../src/api';
import { message } from '../src/messages';
import { OutboxStore } from '../src/outbox-store';
import { advance, OperationQueue, type Job } from '../src/queue';

const job: Job = {
  actorId: 'olga', gameId: 'game-2', key: 'same-key', service: 'intake', path: '/receipts',
  state: 'queued', body: { photo: 'original' }, label: 'Бланк', createdAt: 0,
};
const response = (body: unknown, init?: ResponseInit) => new Response(JSON.stringify(body), init);
const mock = () => ({
  submit: vi.fn(async () => response({ id: 'receipt-1' })),
  read: vi.fn(async () => ({ status: 'Completed', resourceId: 'receipt-1' })) as any,
});

describe('outbox', () => {
  it('reloads by account, repeats the same command after an interruption, and stores no body on completion', async () => {
    const factory = new IDBFactory();
    const client = { ...mock(), actorId: 'olga' };
    const store = new OutboxStore(factory);
    const queue = new OperationQueue(client, store, async (_, submit) => submit());
    const notification = vi.fn();
    queue.changed.addEventListener('change', notification);
    const entry = await queue.enqueue(job.gameId, job.label, job);
    await queue.remove(entry);
    expect(await store.list('another-user')).toEqual([]);
    client.submit.mockRejectedValueOnce(new TypeError('network'));
    await Promise.all([queue.flush(), queue.flush()]);
    expect((await queue.list())[0]).toMatchObject({ key: job.key, body: job.body, state: 'queued' });

    const reopened = new OperationQueue(client, new OutboxStore(factory));
    await reopened.flush();
    const [finished] = await reopened.list();
    expect(finished).toMatchObject({ state: 'done', resultId: 'receipt-1' });
    expect(finished.body).toBeUndefined();
    expect(client.submit.mock.calls[0][0]).toEqual(client.submit.mock.calls[1][0]);
    expect(notification).toHaveBeenCalled();
    await reopened.remove(finished);
    expect(await reopened.list()).toEqual([]);
  });

  it('polls a durable operation and exposes conflicts without switching identity', async () => {
    const server = mock();
    server.submit.mockResolvedValueOnce(response({}, { status: 202, headers: {
      'Operation-Location': '/operations/server-1', 'Operation-Status': 'AwaitingCheckpoint',
    } }));
    const accepted = await advance(job, server);
    expect(accepted.state).toBe('accepted');
    server.read.mockResolvedValueOnce({ status: 'Running' });
    expect(await advance(accepted, server)).toEqual(accepted);
    expect((await advance(accepted, server)).resultId).toBe('receipt-1');
    server.read.mockResolvedValueOnce({ status: 'Failed', lastError: 'ASSIGNMENT_EXPIRED' });
    expect(await advance(accepted, server)).toMatchObject({ state: 'conflict', error: 'ASSIGNMENT_EXPIRED' });
    server.read.mockRejectedValueOnce(new ApiError(404, 'MISSING'));
    expect(await advance(accepted, server)).toMatchObject({ state: 'queued', operation: undefined });
    server.submit.mockResolvedValueOnce(response({ code: 'SESSION_CHANGED' }, { status: 409 }));
    expect(await advance(job, server)).toBe(job);
    server.submit.mockResolvedValueOnce(response({ code: 'REVISION_CONFLICT' }, { status: 409 }));
    expect(await advance(job, server)).toMatchObject({ state: 'conflict', error: 'REVISION_CONFLICT' });
    server.submit.mockResolvedValueOnce(response({ code: 'ERROR' }, { status: 503 }));
    expect(await advance(job, server)).toBe(job);
  });
});

it('forwards operation parameters through the authorized same-origin API', async () => {
  const send = vi.fn<typeof fetch>().mockImplementation(async () => response({ id: '1' }));
  const api = new StaffApi('olga', send);
  expect(await api.command({ ...job, token: 'lease', method: 'PUT' })).toEqual({ id: '1' });
  const [path, options] = send.mock.calls[0];
  expect(path).toBe('/api/intake/receipts');
  expect(new Headers(options?.headers).get('X-Staff-Actor')).toBe('olga');
  expect(new Headers(options?.headers).get('Assignment-Token')).toBe('lease');
  expect(new Headers(options?.headers).get('Idempotency-Key')).toBe(job.key);
  expect(await api.read('intake', '/receipts/1')).toEqual({ id: '1' });
  expect((await api.artifact('intake', '/receipts/1/image')).size).toBeGreaterThan(0);
  expect(await json(new Response(null, { status: 204 }))).toBeUndefined();
  await expect(json(response({ code: 'INACCESSIBLE' }, { status: 403 }))).rejects.toMatchObject({ code: 'INACCESSIBLE' });
  expect(message(new ApiError(409, 'ASSIGNMENT_EXPIRED'))).toMatch(/истёк/);
  expect(message('UNKNOWN')).toMatch(/UNKNOWN/);
  expect(message(null)).toMatch(/подключение/);
});
