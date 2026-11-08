import { spawn } from 'node:child_process';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import net from 'node:net';
import { fileURLToPath } from 'node:url';
const root = resolve(fileURLToPath(new URL('..', import.meta.url)));
const children = [], logs = new Map();
const delay = ms => new Promise(r => setTimeout(r, ms));
async function port() {
  const server = net.createServer();
  await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); });
  const value = server.address().port; await new Promise(r => server.close(r)); return value;
}
function start(command, args, env = {}) {
  const child = spawn(command, args, { cwd: root, env: { ...process.env, ...env }, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
  children.push(child); logs.set(child, '');
  for (const stream of [child.stdout, child.stderr]) stream.on('data', data => logs.set(child, (logs.get(child) + data.toString()).slice(-40000)));
  child.on('error', error => logs.set(child, logs.get(child) + error.stack)); return child;
}
function exitCode(child) {
  return new Promise((resolve, reject) => { child.once('error', reject); child.once('exit', code => resolve(code)); });
}
async function ready(child, url) {
  const deadline = Date.now() + 30000;
  while (Date.now() < deadline) {
    if (child.exitCode !== null) throw new Error('Startup exited: ' + logs.get(child));
    try { if ((await fetch(url + '/health', { signal: AbortSignal.timeout(1000) })).ok) return; } catch {}
    await delay(100);
  }
  throw new Error('Startup timed out: ' + logs.get(child));
}
let directory;
try {
  const build = start('dotnet', ['build', 'Sapientum.Review.sln', '--nologo']);
  if (await exitCode(build)) throw new Error(logs.get(build));
  process.stdout.write(logs.get(build));
  directory = await mkdtemp(join(tmpdir(), 'sapientum-review-'));
  const fixturePort = await port(), servicePort = await port();
  const fixtureUrl = 'http://127.0.0.1:' + fixturePort, serviceUrl = 'http://127.0.0.1:' + servicePort;
  const fixtures = start(process.execPath, ['test-support/fixtures.mjs'], { FIXTURE_PORT: String(fixturePort) });
  await ready(fixtures, fixtureUrl);
  const service = start('dotnet', ['src/Sapientum.Review.Api/bin/Debug/net8.0/Sapientum.Review.Api.dll'], {
    ASPNETCORE_URLS: serviceUrl, ASPNETCORE_ENVIRONMENT: 'ContractTests',
    Dependencies__BaseUrl: fixtureUrl, Dependencies__ClockBaseUrl: fixtureUrl,
    Dependencies__AccessToken: 'test-system', Storage__ConnectionString: 'Data Source=' + join(directory, 'review.db'),
    Runtime__LeaseSeconds: '60', Logging__LogLevel__Default: 'Warning'
  });
  await ready(service, serviceUrl);
  const tests = start(process.execPath, ['--test', '--test-concurrency=1', 'tests/acceptance.test.mjs', 'tests/runtime.test.mjs'], {
    SERVICE_BASE_URL: serviceUrl, FIXTURE_BASE_URL: fixtureUrl, CONTRACT_TEST_ALLOW_WRITES: '1'
  });
  const result = await exitCode(tests); process.stdout.write(logs.get(tests));
  if (result !== 0) throw new Error('Acceptance failed. Service output:\n' + logs.get(service));
} catch (error) { console.error(error.stack); process.exitCode = 1; }
finally {
  for (const child of children.reverse()) if (child.exitCode === null && !child.killed) {
    child.kill(); await Promise.race([new Promise(r => child.once('exit', r)), delay(3000)]);
  }
  if (directory) await rm(directory, { recursive: true, force: true });
}
