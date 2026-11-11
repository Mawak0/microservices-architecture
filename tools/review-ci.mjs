import { spawnSync } from 'node:child_process';
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { resolve } from 'node:path';

const name = process.argv[2];
if (!['materials', 'intake', 'review'].includes(name)) throw new Error('Specify materials, intake or review');
const cwd = resolve('services', name);
const stage = JSON.parse(readFileSync(resolve(cwd, '.review-stage.json'), 'utf8')).stage;
function run(command, args) {
  const result = spawnSync(command, args, { cwd, stdio: 'inherit', windowsHide: true });
  if (result.error) throw result.error;
  if (result.status !== 0) process.exit(result.status ?? 1);
}
const solution = readdirSync(cwd).find(path => path.endsWith('.sln'));
run('dotnet', ['test', solution, '--configuration', 'Release', '--nologo']);
run(process.execPath, ['--test', 'tests/documents.test.mjs', 'tests/harness.test.mjs']);
if (stage === 6) {
  run(process.execPath, ['validation/audit-architecture.mjs', '.']);
  run(process.execPath, ['scripts/run-acceptance.mjs']);
  if (existsSync(resolve(cwd, 'tests/restart-probe.test.mjs')))
    run(process.execPath, ['--test', 'tests/restart-probe.test.mjs']);
}
