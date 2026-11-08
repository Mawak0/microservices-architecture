import { spawn } from 'node:child_process';
import { createServer } from 'node:net';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { beforeRestart, afterRestart } from './reliability-probe.mjs';

const root = fileURLToPath(new URL('../', import.meta.url));
const children = new Set();
const directory = await mkdtemp(join(tmpdir(), 'sapientum-materials-'));
const logs = new Map();
function start(command, args, env, quiet = false) {
  const child = spawn(command, args, { cwd: root, env: { ...process.env, ...env }, windowsHide: true,
    stdio: ['ignore', quiet ? 'pipe' : 'inherit', quiet ? 'pipe' : 'inherit'] });
  children.add(child); logs.set(child, '');
  if (quiet) for (const stream of [child.stdout, child.stderr]) stream.on('data', chunk => logs.set(child, (logs.get(child) + chunk).slice(-30000)));
  child.on('error', error => logs.set(child, `${logs.get(child)}\n${error.message}`));
  return child;
}
async function finish(child, name) {
  const code = await new Promise((resolve, reject) => { child.once('error', reject); child.once('exit', resolve); });
  children.delete(child);
  if (code !== 0) throw new Error(`${name} failed (${code})\n${logs.get(child)}`);
}
async function port() {
  const server = createServer(); await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const result = server.address().port; await new Promise(resolve => server.close(resolve)); return result;
}
async function ready(url, child) {
  const deadline = Date.now() + 60000;
  while (Date.now() < deadline) {
    if (child.exitCode !== null) throw new Error(`Server stopped before readiness\n${logs.get(child)}`);
    try { if ((await fetch(`${url}/health`, { signal: AbortSignal.timeout(1000) })).ok) return; } catch { }
    await new Promise(resolve => setTimeout(resolve, 150));
  }
  throw new Error(`Server did not become ready: ${url}\n${logs.get(child)}`);
}
async function cleanup() {
  for (const child of children) if (child.exitCode === null) child.kill();
  await Promise.all([...children].map(child => child.exitCode !== null ? undefined : new Promise(resolve => { child.once('exit', resolve); setTimeout(resolve, 3000); })));
  await rm(directory, { recursive: true, force: true });
}
try {
  await finish(start('dotnet', ['build', 'Sapientum.Materials.sln', '--configuration', 'Release', '--verbosity', 'minimal'], {}), 'Build');
  await finish(start('dotnet', ['test', 'Sapientum.Materials.sln', '--configuration', 'Release', '--no-build', '--verbosity', 'minimal'], {}), 'Unit/integration/architecture tests');
  const fixtureUrl = `http://127.0.0.1:${await port()}`, serviceUrl = `http://127.0.0.1:${await port()}`;
  const fixture = start(process.execPath, ['test-support/fixtures.mjs'], { FIXTURE_PORT: new URL(fixtureUrl).port }, true);
  await ready(fixtureUrl, fixture);
  const serviceEnvironment = {
    ASPNETCORE_URLS: serviceUrl, ASPNETCORE_ENVIRONMENT: 'ContractTests', Dependencies__BaseUrl: fixtureUrl,
    Dependencies__ClockBaseUrl: fixtureUrl, Dependencies__AccessToken: 'test-system',
    Storage__ConnectionString: `Data Source=${join(directory, 'materials.db')}`
  };
  let service = start('dotnet', ['src/Sapientum.Materials.Api/bin/Release/net8.0/Sapientum.Materials.Api.dll'], serviceEnvironment, true);
  await ready(serviceUrl, service);
  const testEnvironment = { SERVICE_BASE_URL: serviceUrl, FIXTURE_BASE_URL: fixtureUrl, CONTRACT_TEST_ALLOW_WRITES: '1',
    PDF_ARTIFACT_DIR: process.env.PDF_ARTIFACT_DIR || directory };
  try {
    await finish(start(process.execPath, ['--test', '--test-concurrency=1', 'tests/acceptance.test.mjs'], testEnvironment), 'Contract acceptance');
    await finish(start(process.execPath, ['scripts/verify-pdf.mjs'], testEnvironment), 'Rendered PDF/QR verification');
    const probe = await beforeRestart(serviceUrl, fixtureUrl);
    const stopped = new Promise(resolve => service.once('exit', resolve)); service.kill(); await stopped; children.delete(service);
    service = start('dotnet', ['src/Sapientum.Materials.Api/bin/Release/net8.0/Sapientum.Materials.Api.dll'], serviceEnvironment, true);
    await ready(serviceUrl, service);
    await afterRestart(serviceUrl, fixtureUrl, probe);
  } catch (error) { console.error(logs.get(service)); throw error; }
} finally { await cleanup(); }
