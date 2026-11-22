// @vitest-environment node
import { expect, it, vi } from 'vitest';
import { belongsToGame, directoryContains } from '../server/game-access.mjs';

const gameId = 'current';
const check = { id: 'case', gameId };
const authorize = (segment, { service = 'review', rest = [], method = 'GET', query = '', body } = {}, lookup = vi.fn()) =>
  belongsToGame({ service, segment, rest, gameId, method, url: new URL('http://staff/' + query),
    bytes: body ? Buffer.from(JSON.stringify(body)) : undefined }, lookup);

it('authorizes only games from the current catalog and rejects ambiguous filters', async () => {
  const catalog = { tournaments: [{ games: [{ id: gameId }] }] };
  expect(directoryContains(catalog, gameId)).toBe(true);
  expect(directoryContains(catalog, 'foreign')).toBe(false);
  expect(() => directoryContains({})).toThrow();
  expect(await authorize('checks', { rest: ['activity'], query: '?gameId=current' })).toBe(true);
  expect(await authorize('checks', { rest: ['search'], query: '?GameId=foreign&gameId=current' })).toBe(false);
  expect(await authorize('appeal-quotas', { rest: [gameId] })).toBe(true);
  expect(await authorize('appeal-quotas', { rest: ['foreign'], method: 'PUT' })).toBe(false);
  expect(await authorize('checks', { rest: ['next'], method: 'POST', body: { gameId, GameId: 'foreign' } })).toBe(false);
  expect(await authorize('checks', { rest: ['next'], method: 'POST' })).toBe(false);
});

it('binds nested actions and files to the authorized game on the server', async () => {
  const search = vi.fn(async () => ({ items: [check] }));
  expect(await authorize('checks', { rest: ['case', 'verdict'], method: 'POST' }, search)).toBe(true);
  expect(search).toHaveBeenCalledWith('checks/search?gameId=current&id=case&limit=1');
  expect(await authorize('appeals', { method: 'POST', body: { checkId: 'case' } }, search)).toBe(true);
  expect(await authorize('appeals', { method: 'POST', body: { checkId: '../case' } }, search)).toBe(false);
  expect(await authorize('checks', { rest: ['other-case', 'release'], method: 'POST' }, search)).toBe(false);
  expect(await authorize('checks', { rest: ['case', 'renew'], method: 'POST' }, async () => undefined)).toBe(false);
  expect(await authorize('appeals', { rest: ['id', 'resolve'], method: 'POST' }, async () => ({ check }))).toBe(true);
  const read = vi.fn(async () => ({ gameId: 'foreign' }));
  expect(await authorize('receipts', { service: 'intake', rest: ['photo', 'image', 'versions', '1'] }, read)).toBe(false);
  expect(read).toHaveBeenCalledWith('receipts/photo');
  expect(await authorize('print-jobs', { service: 'materials', rest: ['job', 'artifact'] }, async () => ({ gameId }))).toBe(true);
});
