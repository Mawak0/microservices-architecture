import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { PNG } from 'pngjs';
import jsQR from 'jsqr';
import { api, scenario, expectStatus, eventually } from '../test-support/client.mjs';

const s = await scenario({ templateHeading: 'Математическая олимпиада — проверка печати' });
const blank = expectStatus(await api('POST', '/blanks', { body: {
  gameId: s.game.id, teamId: s.teamId, taskId: s.taskIds[0], count: 1, modifier: { kind: 'add', value: 3 }
} }), 201).blanks[0];
const job = expectStatus(await api('POST', '/print-jobs', { body: {
  gameId: s.game.id, teamId: s.teamId, blankIds: [blank.id], taskIds: s.taskIds
} }), 202);
const done = await eventually(() => api('GET', `/print-jobs/${job.id}`), result => result.data.status === 'completed');
assert.equal(done.data.manifest.length, 4);
const bytes = expectStatus(await api('GET', `/print-jobs/${job.id}/artifact`), 200);
const directory = process.env.PDF_ARTIFACT_DIR || 'artifacts'; await mkdir(directory, { recursive: true });
const file = join(directory, 'materials-proof.pdf'); await writeFile(file, bytes);
const child = spawn(process.env.PDF_RENDER_COMMAND || 'pdftoppm', ['-f', '1', '-singlefile', '-scale-to', '2200', '-png', file], { windowsHide: true });
const chunks = [], errors = []; child.stdout.on('data', c => chunks.push(c)); child.stderr.on('data', c => errors.push(c));
const code = await new Promise((resolve, reject) => { child.once('error', reject); child.once('exit', resolve); });
assert.equal(code, 0, Buffer.concat(errors).toString());
const rendered = Buffer.concat(chunks); const png = PNG.sync.read(rendered);
await writeFile(join(directory, 'materials-proof.png'), rendered);
const decoded = jsQR(new Uint8ClampedArray(png.data), png.width, png.height, { inversionAttempts: 'dontInvert' });
assert.equal(decoded?.data, blank.qrToken, 'The QR must decode from the actual rasterized PDF page.');
console.log(`Real PDF rendered, signed QR independently decoded: ${file}`);
