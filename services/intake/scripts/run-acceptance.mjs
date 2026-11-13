import { postgresConnection } from '../test-support/postgres.mjs';
import { spawn } from 'node:child_process';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import net from 'node:net';
import { fileURLToPath } from 'node:url';
import { runRestartProbe } from './restart-probe.mjs';

const root = fileURLToPath(new URL('..', import.meta.url));
const children = [];
const freePort = () => new Promise((resolve, reject) => {
  const socket = net.createServer(); socket.on('error', reject);
  socket.listen(0, '127.0.0.1', () => { const port = socket.address().port; socket.close(() => resolve(port)); });
});
async function run(command, args, env = process.env) {
  const child = spawn(command, args, { cwd: root, env, windowsHide: true, stdio: 'inherit' });
  const exitCode = await new Promise((resolve, reject) => { child.once('error', reject); child.once('exit', resolve); });
  if (exitCode !== 0) throw new Error(`${command} exited with ${exitCode}`);
}
function start(command, args, env) {
  const child = spawn(command, args, { cwd: root, env, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
  child.output = ''; child.failure = null;
  child.on('error', error => { child.failure = error; });
  for (const pipe of [child.stdout, child.stderr]) pipe.on('data', data => { child.output = (child.output + data.toString()).slice(-24000); });
  children.push(child); return child;
}
async function ready(child, base) {
  const deadline = Date.now() + 30000;
  while (Date.now() < deadline) {
    if (child.failure || child.exitCode !== null || child.signalCode !== null) throw new Error(`Startup failed: ${child.failure || child.output}`);
    try { if ((await fetch(`${base}/health`, { signal: AbortSignal.timeout(1000) })).ok) return; } catch {}
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  throw new Error(`Startup timeout: ${child.output}`);
}
const directory = await mkdtemp(join(tmpdir(), 'olympiad-intake-'));
try {
  await run('dotnet', ['build', 'Olympiad.Intake.sln', '--nologo']);
  const fixturePort = await freePort(), servicePort = await freePort();
  const fixtureBase = `http://127.0.0.1:${fixturePort}`, serviceBase = `http://127.0.0.1:${servicePort}`;
  const fixture = start(process.execPath, ['test-support/fixtures.mjs'], { ...process.env, FIXTURE_PORT: String(fixturePort) });
  await ready(fixture, fixtureBase);
  const serviceEnvironment = {
    ...process.env, ASPNETCORE_ENVIRONMENT: 'ContractTests', Workflows__Enabled: 'false', ASPNETCORE_URLS: serviceBase,
    Dependencies__BaseUrl: fixtureBase, Dependencies__ClockBaseUrl: fixtureBase, Dependencies__AccessToken: 'test-system',
    Storage__ConnectionString: await postgresConnection(directory),
    Logging__LogLevel__Default: 'Warning'
  };
  let service;
  const launchService = async () => {
    service = start('dotnet', ['src/Olympiad.Intake.Api/bin/Debug/net8.0/Olympiad.Intake.Api.dll'], serviceEnvironment);
    await ready(service, serviceBase);
  };
  await launchService();
  await run(process.execPath, ['--test', '--test-concurrency=1', 'tests/acceptance.test.mjs'], {
    ...process.env, SERVICE_BASE_URL: serviceBase, FIXTURE_BASE_URL: fixtureBase, CONTRACT_TEST_ALLOW_WRITES: '1'
  });
  await runRestartProbe({ serviceBase, fixtureBase, restart: async () => {
    const stopped = new Promise(resolve => service.once('exit', resolve));
    service.kill('SIGTERM'); await stopped; await launchService();
  } });
} catch (error) {
  for (const child of children) if (child.output) process.stderr.write(child.output);
  process.stderr.write(`${error.stack}\n`); process.exitCode = 1;
} finally {
  for (const child of children.reverse()) {
    if (child.exitCode === null && child.signalCode === null) {
      const stopped = new Promise(resolve => child.once('exit', resolve)); child.kill('SIGTERM'); await stopped;
    }
  }
  await rm(directory, { recursive: true, force: true });
}
