import { postgresConnection } from '../test-support/postgres.mjs';
import assert from 'node:assert/strict';
import http from 'node:http';
import net from 'node:net';
import os from 'node:os';
import path from 'node:path';
import fs from 'node:fs/promises';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { randomUUID } from 'node:crypto';
import QRCode from 'qrcode';
import { createFixtureServer } from '../test-support/fixtures.mjs';

const args = new Map();
for (let i = 2; i < process.argv.length; i += 2) args.set(process.argv[i], process.argv[i + 1]);
const services = ['materials', 'intake', 'review'];
const roots = Object.fromEntries(services.map(name => {
  const root = args.get(`--${name}`);
  assert.ok(root, `Required: --${name} <service-worktree>`);
  return [name, path.resolve(root)];
}));
const temp = await fs.mkdtemp(path.join(os.tmpdir(), 'olympiad-integration-'));
const fixture = createFixtureServer();
const children = new Map(), logs = new Map(), urls = {};
const images = new Map(), printedImages = new Map();
let fixtureUrl, bridgeUrl, gameDependencyUnavailable = false;

async function listen(server) {
  server.listen(0, '127.0.0.1'); await once(server, 'listening');
  return `http://127.0.0.1:${server.address().port}`;
}

async function freePort() {
  const server = net.createServer(); server.listen(0, '127.0.0.1'); await once(server, 'listening');
  const port = server.address().port;
  await new Promise(resolve => server.close(resolve)); return port;
}

async function call(base, method, route, body, { role = 'admin', assignmentToken, key = randomUUID() } = {}) {
  const headers = { authorization: `Bearer test-${role}` };
  if (['POST', 'PUT'].includes(method)) headers['idempotency-key'] = key;
  if (assignmentToken) headers['assignment-token'] = assignmentToken;
  if (body !== undefined) headers['content-type'] = 'application/json';
  const response = await fetch(`${base}${route}`, {
    method, headers, body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(15000)
  });
  const bytes = Buffer.from(await response.arrayBuffer());
  const data = response.headers.get('content-type')?.includes('json') ? JSON.parse(bytes.toString()) : bytes;
  return { status: response.status, data, headers: response.headers };
}

function success(response, statuses = [200, 201, 202]) {
  assert.ok(statuses.includes(response.status), `HTTP ${response.status}: ${JSON.stringify(response.data)}`);
  return response.data;
}

async function eventually(operation, predicate) {
  const end = Date.now() + 30000;
  while (Date.now() < end) {
    const value = await operation(); if (predicate(value)) return value;
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  throw new Error('Timed out waiting for an integration condition.');
}

const bridge = http.createServer(async (req, res) => {
  try {
    const route = req.url;
    if (gameDependencyUnavailable && /^\/games\//.test(route)) {
      res.writeHead(503, { 'content-type': 'application/json' });
      res.end(JSON.stringify({ code: 'GAME_UNAVAILABLE', message: 'Intentional integration outage.' })); return;
    }
    const chunks = []; for await (const chunk of req) chunks.push(chunk);
    const bytes = Buffer.concat(chunks);
    if (route === '/images' && req.method === 'POST') {
      // Independent test object store: actual material IDs are not fixture blank IDs.
      const image = JSON.parse(bytes.toString());
      assert.ok(image.receiptId && image.blankId && image.dataBase64 && image.mediaType);
      const filename = Buffer.from(image.receiptId).toString('base64url');
      const storedBytes = Buffer.from(image.dataBase64, 'base64');
      await fs.mkdir(path.join(temp, 'images'), { recursive: true });
      await fs.writeFile(path.join(temp, 'images', filename), storedBytes);
      images.set(image.receiptId, { bytes: storedBytes, mediaType: image.mediaType });
      res.writeHead(201, { 'content-type': 'application/json' });
      res.end(JSON.stringify({ imageRef: `${bridgeUrl}/images/${encodeURIComponent(image.receiptId)}` })); return;
    }
    if (route.startsWith('/images/') && req.method === 'GET') {
      const image = images.get(decodeURIComponent(route.slice('/images/'.length)));
      if (!image) { res.writeHead(404); res.end(); return; }
      res.writeHead(200, { 'content-type': image.mediaType }); res.end(image.bytes); return;
    }
    const headers = { ...req.headers };
    for (const name of ['host', 'content-length', 'connection', 'transfer-encoding', 'keep-alive', 'upgrade', 'te', 'trailer', 'expect'])
      delete headers[name];
    let target = fixtureUrl;
    if (route === '/blank-verifications') target = urls.materials;
    if (/^\/receipts\/[^/?]+$/.test(route)) target = urls.intake;
    const upstream = await fetch(`${target}${route}`, {
      method: req.method, headers, body: ['GET', 'HEAD'].includes(req.method) ? undefined : bytes,
      signal: AbortSignal.timeout(10000)
    });
    const payload = Buffer.from(await upstream.arrayBuffer());
    if (route === '/events' && upstream.ok) {
      const event = JSON.parse(bytes.toString());
      const destination = event.type === 'ReceiptAccepted' ? '/integrations/receipts'
        : event.type === 'IntakeClosed' ? '/integrations/intake-closures' : null;
      if (destination) {
        const delivered = await call(urls.review, 'POST', destination, event, { role: 'system', key: event.eventId });
        if (![200, 201, 202].includes(delivered.status)) {
          res.writeHead(503, { 'content-type': 'application/json' });
          res.end(JSON.stringify({ code: 'DELIVERY_FAILED', message: JSON.stringify(delivered.data) })); return;
        }
      }
    }
    res.writeHead(upstream.status, { 'content-type': upstream.headers.get('content-type') || 'application/octet-stream' });
    res.end(payload);
  } catch (error) {
    res.writeHead(503, { 'content-type': 'application/json' });
    res.end(JSON.stringify({ code: 'BRIDGE_UNAVAILABLE', message: error.message }));
  }
});

async function waitForExit(child) {
  if (child.exitCode !== null) return;
  await Promise.race([once(child, 'exit'), new Promise(resolve => setTimeout(resolve, 3000))]);
}

async function stopService(name) {
  const child = children.get(name); if (!child) return;
  child.kill(); await waitForExit(child); children.delete(name);
}

async function startService(name) {
  const label = name[0].toUpperCase() + name.slice(1);
  const dll = path.join(roots[name], 'src', `Olympiad.${label}.Api`, 'bin', 'Debug', 'net8.0', `Olympiad.${label}.Api.dll`);
  const child = spawn('dotnet', [dll], {
    cwd: roots[name], windowsHide: true,
    env: { ...process.env, ASPNETCORE_URLS: urls[name], ASPNETCORE_ENVIRONMENT: 'ContractTests', Workflows__Enabled: 'false', DOTNET_ENVIRONMENT: 'ContractTests',
      Dependencies__BaseUrl: bridgeUrl, Dependencies__ClockBaseUrl: fixtureUrl, Dependencies__AccessToken: 'test-system',
      Storage__ConnectionString: await postgresConnection(path.join(temp, name)), Runtime__LeaseSeconds: '60' },
    stdio: ['ignore', 'pipe', 'pipe']
  });
  children.set(name, child); logs.set(name, '');
  for (const stream of [child.stdout, child.stderr]) stream.on('data', data => logs.set(name, (logs.get(name) + data).slice(-20000)));
  child.on('error', error => logs.set(name, logs.get(name) + error.message));
  await eventually(async () => {
    if (child.exitCode !== null) throw new Error(`${name} exited: ${logs.get(name)}`);
    try { return (await fetch(`${urls[name]}/health`, { signal: AbortSignal.timeout(1000) })).ok; }
    catch { return false; }
  }, ready => ready);
}

async function build(name) {
  const label = name[0].toUpperCase() + name.slice(1);
  const project = path.join(roots[name], 'src', `Olympiad.${label}.Api`, `Olympiad.${label}.Api.csproj`);
  const child = spawn('dotnet', ['build', project, '--nologo'], { cwd: roots[name], windowsHide: true, stdio: 'inherit' });
  const [code] = await once(child, 'exit'); assert.equal(code, 0, `${name} build failed`);
}

try {
  for (const name of services) { await build(name); urls[name] = `http://127.0.0.1:${await freePort()}`; }
  fixtureUrl = await listen(fixture); bridgeUrl = await listen(bridge);
  for (const name of services) await startService(name);

  const s = success(await call(fixtureUrl, 'POST', '/__test/scenarios', { system: 'bonuses', quota: 1 }));
  const api = (name, method, route, body, options) => call(urls[name], method, route, body, options);
  success(await api('materials', 'POST', '/kits', { gameId: s.game.id, teamId: s.teamId }));
  const issuanceCommand = {
    gameId: s.game.id, teamId: s.teamId, taskId: s.taskIds[0], count: 2, modifier: { kind: 'add', value: 3 }
  }, issuanceKey = randomUUID();
  const issued = success(await api('materials', 'POST', '/blanks', issuanceCommand, { key: issuanceKey }));
  const job = success(await api('materials', 'POST', '/print-jobs', {
    gameId: s.game.id, teamId: s.teamId, blankIds: issued.blanks.map(b => b.id), taskIds: [s.taskIds[0]]
  }));
  await eventually(() => api('materials', 'GET', `/print-jobs/${job.id}`), r => r.status === 200 && r.data.status === 'completed');
  const pdf = await api('materials', 'GET', `/print-jobs/${job.id}/artifact`, undefined, { role: 'observer' });
  assert.equal(pdf.status, 200); assert.equal(pdf.data.subarray(0, 5).toString(), '%PDF-');
  if (args.get('--artifacts')) {
    const directory = path.resolve(args.get('--artifacts'));
    await fs.mkdir(directory, { recursive: true });
    await fs.writeFile(path.join(directory, 'materials.pdf'), pdf.data);
    await fs.writeFile(path.join(directory, 'expected-materials.json'), JSON.stringify({
      qrTokens: issued.blanks.map(b => b.qrToken), taskNumber: '1', heading: s.game.materialSettings.templateParameters.heading,
      statement: 'Задача 1: вычислите 2 + 2'
    }, null, 2));
    if (args.get('--render-pdf') === 'true') {
      const renderer = spawn(args.get('--pdftoppm') || 'pdftoppm', ['-r', '150', '-png', path.join(directory, 'materials.pdf'), path.join(directory, 'sheet')],
        { windowsHide: true, stdio: 'inherit' });
      const [exitCode] = await once(renderer, 'exit'); assert.equal(exitCode, 0, 'PDF rendering failed');
      for (let i = 0; i < issued.blanks.length; i++)
        printedImages.set(issued.blanks[i].id, (await fs.readFile(path.join(directory, `sheet-${i + 1}.png`))).toString('base64'));
    }
  }
  success(await api('review', 'PUT', `/appeal-quotas/${s.game.id}`, { limit: 1 }));
  success(await api('intake', 'PUT', `/intake-sessions/${s.game.id}`, { admissionVersion: s.game.admissionVersion }));

  async function submit(blank, options = {}) {
    const image = printedImages.get(blank.id) ?? (await QRCode.toDataURL(blank.qrToken, { margin: 4, scale: 6 })).split(',')[1];
    return api('intake', 'POST', '/receipts', {
      qrToken: blank.qrToken, imageBase64: image, imageMediaType: 'image/png', legibilityConfirmed: true
    }, { role: 'observer', ...options });
  }
  async function checkFor(receipt) {
    const response = await eventually(() => api('review', 'GET', `/checks?gameId=${s.game.id}`, undefined, { role: 'checker' }),
      r => r.status === 200 && r.data.items.some(c => c.receiptId === receipt.id));
    return response.data.items.find(c => c.receiptId === receipt.id);
  }
  const savedVerdicts = new Map();
  async function judge(check, verdict) {
    const assigned = success(await api('review', 'POST', `/checks/${check.id}/claim`, undefined, { role: 'checker' }));
    const options = { role: 'checker', assignmentToken: assigned.lease.token, key: randomUUID() };
    const result = success(await api('review', 'POST', `/checks/${check.id}/verdict`, { verdict }, options));
    savedVerdicts.set(check.id, { body: { verdict }, options, result }); return result;
  }
  const receiptKey = randomUUID();
  const firstReceipt = success(await submit(issued.blanks[0], { key: receiptKey }));
  const firstCheck = await judge(await checkFor(firstReceipt), 'incorrect');
  assert.equal(firstCheck.taskId, s.taskIds[0]); assert.equal(firstCheck.referenceAnswer, '4');
  const appeal = success(await api('review', 'POST', '/appeals', { checkId: firstCheck.id, reason: 'Пересмотр ответа' }, { role: 'observer' }));
  const assignedAppeal = success(await api('review', 'POST', `/appeals/${appeal.id}/claim`, undefined, { role: 'checker-2' }));
  const resolved = success(await api('review', 'POST', `/appeals/${appeal.id}/resolve`, {
    outcome: 'jury-error-corrected', verdict: 'correct', reason: 'Ошибка жюри исправлена'
  }, { role: 'checker-2', assignmentToken: assignedAppeal.check.lease.token }));
  assert.equal(resolved.check.originalVerdict, 'incorrect'); assert.equal(resolved.check.history.length, 2);
  assert.equal(success(await api('review', 'GET', `/appeal-quotas/${s.game.id}`)).remaining, 1);
  assert.equal((await submit(issued.blanks[0])).data.code, 'BLANK_ALREADY_ACCEPTED');

  async function changeGame(changes) {
    return success(await call(fixtureUrl, 'PATCH', `/__test/scenarios/${s.game.id}`, changes)).game;
  }
  async function close(game) {
    return success(await api('intake', 'POST', `/intake-sessions/${s.game.id}/close`, { admissionVersion: game.admissionVersion }, { role: 'system' }));
  }
  const completion = () => api('review', 'GET', `/games/${s.game.id}/completion`, undefined, { role: 'system' });
  let game = await changeGame({ submissionOpen: false, appealOpen: false, finishExpired: true });
  await close(game); await eventually(completion, r => r.status === 200 && r.data.complete);

  game = await changeGame({ lateSubmissionAllowed: true });
  success(await api('intake', 'PUT', `/intake-sessions/${s.game.id}`, { admissionVersion: game.admissionVersion }));
  assert.equal(success(await completion()).complete, false);
  success(await call(fixtureUrl, 'PUT', '/__test/event-sink', { available: false }));
  const secondReceipt = success(await submit(issued.blanks[1]));
  await stopService('intake'); await startService('intake');
  assert.equal(success(await api('intake', 'GET', `/receipts/${secondReceipt.id}`)).blankId, issued.blanks[1].id);
  success(await call(fixtureUrl, 'PUT', '/__test/event-sink', { available: true }));
  await judge(await checkFor(secondReceipt), 'correct');
  game = await changeGame({ lateSubmissionAllowed: false });
  const closed = await close(game);
  assert.deepEqual([...closed.receiptIds].sort(), [firstReceipt.id, secondReceipt.id].sort());
  await eventually(completion, r => r.status === 200 && r.data.complete);

  for (const name of services) { await stopService(name); await startService(name); }
  assert.equal(success(await api('materials', 'GET', `/blanks/${issued.blanks[0].id}`)).qrToken, issued.blanks[0].qrToken);
  assert.equal(success(await api('intake', 'GET', `/receipts/${firstReceipt.id}`)).id, firstReceipt.id);
  const retained = success(await api('review', 'GET', `/checks/${firstCheck.id}`, undefined, { role: 'checker' }));
  assert.equal(retained.originalVerdict, 'incorrect'); assert.equal(retained.verdict, 'correct');
  assert.equal(retained.history.length, 2); assert.equal(success(await completion()).complete, true);
  gameDependencyUnavailable = true;
  try {
    assert.deepEqual(success(await api('materials', 'POST', '/blanks', issuanceCommand, { key: issuanceKey })), issued);
    assert.deepEqual(success(await submit(issued.blanks[0], { key: receiptKey })), firstReceipt);
    const saved = savedVerdicts.get(firstCheck.id);
    assert.deepEqual(success(await api('review', 'POST', `/checks/${firstCheck.id}/verdict`, saved.body, saved.options)), saved.result);
  } finally { gameDependencyUnavailable = false; }
  game = await changeGame({ lateSubmissionAllowed: true });
  success(await api('intake', 'PUT', `/intake-sessions/${s.game.id}`, { admissionVersion: game.admissionVersion }));
  assert.equal((await submit(issued.blanks[0])).data.code, 'BLANK_ALREADY_ACCEPTED');
  game = await changeGame({ lateSubmissionAllowed: false });
  await close(game); await eventually(completion, r => r.status === 200 && r.data.complete);
  console.log('PASS: real materials → intake → review → appeal → late submission → cumulative completion.');
  console.log('PASS: pending intake event survives restart while event sink is unavailable.');
  console.log('PASS: all three services preserve state across process restart.');
  console.log('PASS: persisted successful request replays survive restart and game dependency outage.');
} catch (error) {
  for (const [name, output] of logs) console.error(`${name}:\n${output}`);
  throw error;
} finally {
  for (const name of services) await stopService(name);
  await new Promise(resolve => bridge.close(resolve));
  await new Promise(resolve => fixture.close(resolve));
  await fs.rm(temp, { recursive: true, force: true });
}
