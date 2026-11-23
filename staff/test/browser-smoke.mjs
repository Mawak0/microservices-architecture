import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { once } from 'node:events';
import { setTimeout as delay } from 'node:timers/promises';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';
import { app } from '../server/app.mjs';
import { Sessions } from '../server/session.mjs';

const demo = 'http://127.0.0.1:19180';
const services = {
  materials: 'http://127.0.0.1:19181', intake: 'http://127.0.0.1:19182', review: 'http://127.0.0.1:19183',
};

async function api(url, { body, role = 'admin', ...options } = {}) {
  const response = await fetch(url, {
    method: body ? 'POST' : 'GET', signal: AbortSignal.timeout(30_000), ...options,
    headers: { 'Content-Type': 'application/json', 'X-Demo-Role': role },
    body: body && JSON.stringify(body),
  });
  const value = await response.text();
  assert.ok(response.ok, `${url} returned ${response.status}: ${value}`);
  return value && JSON.parse(value);
}

async function waitFor(description, read) {
  const deadline = Date.now() + 20_000;
  while (Date.now() < deadline) {
    const result = await read();
    if (result) return result;
    await delay(100);
  }
  assert.fail(description);
}

const game = await api(demo + '/demo/game', { body: { name: 'Браузерный сценарий', system: 'bonuses' } });
assert.equal(game.teams.length, 3);
async function issuePhoto(game, task, teamId = game.teamId) {
  const { blanks: [blank] } = await api(demo + '/api/materials/blanks', {
    body: { gameId: game.id, teamId, taskId: task.id, count: 1 },
  });
  assert.equal(blank.teamId, teamId);
  const { image } = await api(demo + '/demo/qr', { body: { token: blank.qrToken } });
  return { blank, base64: image.split(',')[1] };
}

function submitPhoto(game, photo) {
  return api(demo + '/api/intake/receipts', { role: 'observer', body: {
    gameId: game.id, qrToken: photo.blank.qrToken, imageBase64: photo.base64,
    imageMediaType: 'image/png', legibilityConfirmed: true,
  } });
}

const [first, second] = await Promise.all(game.tasks.slice(0, 2)
  .map((task, index) => issuePhoto(game, task, game.teams[index].id)));
const contextUrl = demo + '/staff-test-context';
const directory = { tournaments: [{ id: game.tournamentId, name: 'Олимпиада', games: [{
  id: game.id, name: game.name, tasks: game.tasks, teams: game.teams,
}] }] };
const sessions = new Sessions();
let handle;
const server = createServer((request, response) => { void handle(request, response); });
server.listen(0, '127.0.0.1');
await once(server, 'listening');
const origin = new URL(`http://127.0.0.1:${server.address().port}`);
handle = app({ origin, identityUrl: demo + '/auth/introspect', directoryUrl: contextUrl, services }, {}, {
  sessions, root: fileURLToPath(new URL('../dist/', import.meta.url)),
  send: (url, options) => url === contextUrl ? Promise.resolve(Response.json(directory)) : fetch(url, options),
});

let browser;
try {
  browser = await chromium.launch({ executablePath: process.env.STAFF_TEST_BROWSER });
  const sample = await browser.newPage();
  await sample.goto(demo + '/health');
  const filledSheet = await sample.evaluate(async ({ base64, fields }) => {
    const { drawAnswerSheet } = await import('/answer-sheet.js');
    const qr = new Image();
    qr.src = 'data:image/png;base64,' + base64;
    await qr.decode();
    const canvas = document.createElement('canvas');
    drawAnswerSheet(canvas, qr, fields);
    return canvas.toDataURL('image/png').split(',')[1];
  }, { base64: first.base64, fields: {
    teamName: game.teams[0].name, blankNumber: first.blank.number, taskNumber: first.blank.taskNumber,
    statement: game.tasks[0].statement, answer: game.tasks[0].referenceAnswer,
  } });
  await sample.close();
  async function workspace(role) {
    const context = await browser.newContext();
    context.setDefaultTimeout(20_000);
    const token = 'demo-' + role;
    await context.addCookies([{ name: 'staff-session', value: sessions.create({ actorId: token, token }, 300), url: origin.href }]);
    return context;
  }
  async function selectGame(page) {
    const response = await page.goto(origin.href);
    assert.ok(response?.ok(), `The cabinet page returned ${response?.status()}`);
    const picker = page.getByRole('banner').getByRole('combobox');
    try { await picker.selectOption(game.id); }
    catch (error) { throw new Error(`The game picker could not open: ${await page.locator('body').innerText()}`, { cause: error }); }
  }

  const observer = await workspace('observer');
  const inaccessible = await observer.request.get(new URL('/api/intake/receipts?gameId=outside', origin).href, {
    headers: { 'X-Staff-Actor': 'demo-observer', 'X-Staff-Game': 'outside', 'If-None-Match': '*' },
  });
  assert.equal(inaccessible.status(), 403);
  const submissions = [];
  let discard = true;
  let firstStatus;
  await observer.route('**/api/intake/receipts', async route => {
    const request = route.request();
    if (request.method() !== 'POST') return route.continue();
    submissions.push({ key: request.headers()['idempotency-key'], body: request.postData() });
    if (!discard) return route.continue();
    if (submissions.length === 1) {
      const response = await route.fetch();
      const status = response.status();
      await response.dispose();
      await route.abort('connectionreset');
      firstStatus = status;
      return;
    }
    await route.abort('connectionreset');
  });
  const capture = await observer.newPage();
  await selectGame(capture);
  const preview = capture.waitForResponse(response => new URL(response.url()).pathname === '/api/intake/receipts/preview');
  await capture.getByLabel('Загрузить фото').setInputFiles({
    name: 'answer.png', mimeType: 'image/png', buffer: Buffer.from(filledSheet, 'base64'),
  });
  const previewResponse = await preview.catch(async error => {
    throw new Error(`QR preview did not run: ${await capture.locator('body').innerText()}`, { cause: error });
  });
  assert.ok(previewResponse.ok(), `QR preview returned ${previewResponse.status()}: ${await previewResponse.text()}`);
  await capture.getByText('· задача ' + game.tasks[0].number, { exact: false }).waitFor();
  await capture.getByRole('button', { name: 'Снимок чёткий — отправить' }).click();
  await waitFor('The server did not accept the original submission', () => firstStatus);
  assert.ok(firstStatus < 300, `The original submission returned ${firstStatus}`);
  await observer.setOffline(true);
  await capture.getByText('Нет связи', { exact: true }).waitFor();
  await observer.setOffline(false);
  await capture.reload();
  await capture.getByText('Отправка · 1', { exact: true }).waitFor();
  const otherTab = await observer.newPage();
  await selectGame(otherTab);
  discard = false;
  await capture.getByText('Отправка · 0', { exact: true }).waitFor();
  await capture.getByText('Отправка · 0', { exact: true }).click();
  await capture.getByText(/Бланк .* · Завершено/).waitFor();
  await otherTab.getByText('Отправка · 0', { exact: true }).waitFor();
  assert.ok(submissions.length >= 2);
  assert.ok(submissions[0].key);
  assert.ok(submissions.every(entry => entry.key === submissions[0].key && entry.body === submissions[0].body));
  const receipts = await api(demo + '/api/intake/receipts?gameId=' + game.id);
  assert.deepEqual(receipts.items.map(receipt => receipt.blankId), [first.blank.id]);
  console.log('PASS Браузер восстановил принятую отправку после потери ответа и перезагрузки в двух вкладках');

  await submitPhoto(game, second);
  const checks = () => api(demo + '/api/review/checks?gameId=' + game.id).then(page => page.items);
  await waitFor('Both answers did not reach the review queue', async () => (await checks()).length === 2);
  const otherGame = await api(demo + '/demo/game', { body: { name: 'Активное назначение', system: 'bonuses' } });
  await submitPhoto(otherGame, await issuePhoto(otherGame, otherGame.tasks[0]));
  await waitFor('The other game did not receive its answer', async () =>
    (await api(demo + '/api/review/checks?gameId=' + otherGame.id)).items.length === 1);
  const held = await api(demo + '/api/review/checks/next', { role: 'checker-2', body: { gameId: otherGame.id } });
  assert.ok(held?.id);
  const checker = await workspace('checker-2');
  const review = await checker.newPage();
  const replies = [];
  review.on('response', response => {
    const path = new URL(response.url()).pathname;
    if (path.startsWith('/api/') && response.request().method() === 'POST')
      replies.push([path, response.status(), response.headers()['operation-status']]);
  });
  await selectGame(review);
  await review.getByRole('button', { name: 'Взять следующую карточку' }).click();
  try {
    await review.getByRole('img', { name: /Бланк задачи/ }).waitFor();
  } catch (error) {
    throw new Error(`The card photo is missing: ${await review.locator('body').innerText()}\n${JSON.stringify(replies)}`, { cause: error });
  }
  const current = await review.getByRole('heading', { name: /^Задача / }).innerText();
  await waitFor('The prefetch did not encounter the assignment limit', () =>
    replies.some(([path, status]) => path === '/api/review/checks/next' && status === 204));
  await review.keyboard.down('1');
  await review.getByRole('heading', { name: current, exact: true }).waitFor({ state: 'hidden' });
  try {
    await review.getByRole('img', { name: /Бланк задачи/ }).waitFor();
  } catch (error) {
    throw new Error(`The next photo is missing: ${await review.locator('body').innerText()}\n${JSON.stringify(replies)}`, { cause: error });
  }
  await review.keyboard.down('1');
  await review.keyboard.up('1');
  const decided = await waitFor('The verdict was not committed', async () => {
    const items = await checks();
    return items.some(item => item.verdict === 'correct') && items;
  });
  assert.equal(decided.filter(item => item.verdict).length, 1);
  await review.getByRole('button', { name: 'Вернуть задание' }).click();
  await review.getByRole('button', { name: 'Взять следующую карточку' }).waitFor();
  assert.equal((await checks()).filter(item => item.verdict).length, 1);
  console.log('PASS Браузер записал один вердикт и открыл следующую карточку после освобождения назначения');
} finally {
  await browser?.close();
  await new Promise((resolve, reject) => server.close(error => error ? reject(error) : resolve()));
}
