import http from 'node:http';
import { randomUUID } from 'node:crypto';
import { pathToFileURL } from 'node:url';

const roles = {
  'test-admin': ['admin'], 'test-observer': ['observer'],
  'test-checker': ['checker'], 'test-checker-2': ['checker'],
  'test-organizer': ['organizer'], 'test-multi': ['observer', 'checker'],
  'test-system': ['integration']
};

export function createFixtureServer() {
  const scenarios = new Map(), teams = new Map(), tasks = new Map();
  const blanks = new Map(), receipts = new Map(), images = new Map(), events = new Map();
  let sinkAvailable = true;
  const builtinProfiles = {
    bonuses: { appealsAllowed: true, modifiersAllowed: true, taskCardsShared: false },
    domino: { appealsAllowed: false, modifiersAllowed: false, taskCardsShared: true },
    'tic-tac-toe': { appealsAllowed: true, modifiersAllowed: false, taskCardsShared: false }
  };
  const reply = (res, status, body) => {
    res.writeHead(status, { 'content-type': 'application/json' }); res.end(JSON.stringify(body));
  };
  const error = (res, status, code) => reply(res, status, { code, message: code });
  function update(s, changes) {
    const changed = ['submissionOpen', 'lateSubmissionAllowed'].some(k => k in changes && changes[k] !== s.options[k]);
    if (changed) s.game.admissionVersion++;
    Object.assign(s.options, changes);
    const o = s.options, now = Date.now(), deadline = open => new Date(now + (open ? 3600000 : -3600000)).toISOString();
    Object.assign(s.game, {
      system: o.system, status: o.submissionOpen ? 'running' : 'submission-closed',
      submissionDeadline: deadline(o.submissionOpen), appealDeadline: deadline(o.appealOpen),
      finishDeadline: deadline(!o.finishExpired), lateSubmissionAllowed: o.lateSubmissionAllowed,
      capabilities: { appealsAllowed: o.appealsAllowed, modifiersAllowed: o.modifiersAllowed },
      materialSettings: { answerBlankCount: o.answerBlankCount, taskCardCount: o.taskCardCount, taskCardsShared: o.taskCardsShared,
        templateKey: o.templateKey, templateParameters: { heading: o.templateHeading } }
    });
    for (const id of [s.teamId, s.otherTeamId]) teams.get(id).admitted = o.admitted;
  }
  function scenario(options) {
    const gameId = randomUUID(), teamId = randomUUID(), otherTeamId = randomUUID();
    const taskIds = Array.from({ length: 3 }, () => randomUUID());
    const game = { id: gameId, tournamentId: randomUUID(), admissionVersion: 1, taskIds, taskAssignments: taskIds.map((taskId, i) => ({ taskId, taskNumber: String(i + 1) })) };
    const s = { game, teamId, otherTeamId, taskIds, blankIds: [], qrTokens: [], receipts: [], options: {
      system: 'bonuses', submissionOpen: true, appealOpen: true, finishExpired: false,
      quota: 2, admitted: true, storageAvailable: true, answerBlankCount: 3, taskCardCount: 3,
      lateSubmissionAllowed: false, appealsAllowed: false, modifiersAllowed: false, taskCardsShared: false,
      ...(builtinProfiles[options.system || 'bonuses'] || {}),
      templateKey: `${options.system || 'bonuses'}/v1`, templateHeading: 'Бланк ответа', ...options
    } };
    teams.set(teamId, { id: teamId, gameId, admitted: true });
    teams.set(otherTeamId, { id: otherTeamId, gameId, admitted: true });
    taskIds.forEach((id, i) => tasks.set(id, { id, statement: `Задача ${i + 1}: вычислите 2 + ${i + 2}`, referenceAnswer: String(i + 4) }));
    update(s, {});
    for (let i = 0; i < 8; i++) {
      const id = randomUUID(), qrToken = `fixture-signed:${id}`;
      const taskIndex = i % 4 < 2 ? 0 : i % 4 - 1;
      const b = { id, number: i + 1, gameId, teamId: i < 4 ? teamId : otherTeamId,
        taskId: taskIds[taskIndex], taskNumber: String(taskIndex + 1), qrToken,
        modifier: s.options.modifiersAllowed ? { kind: 'add', value: 3 } : null };
      blanks.set(qrToken, b); s.blankIds.push(id); s.qrTokens.push(qrToken);
      const receipt = { id: randomUUID(), blankId: id, gameId, admissionVersion: game.admissionVersion, teamId: b.teamId, taskId: b.taskId, taskNumber: b.taskNumber,
        imageRef: `fixture://images/${id}`, acceptedAt: new Date(Date.now() - 7200000).toISOString() };
      receipts.set(receipt.id, receipt); s.receipts.push(receipt);
    }
    scenarios.set(gameId, s); return s;
  }
  return http.createServer(async (req, res) => {
    try {
      const url = new URL(req.url, 'http://fixture'), path = url.pathname;
      let raw = ''; for await (const chunk of req) raw += chunk;
      const body = raw ? JSON.parse(raw) : {};
      if (req.method === 'GET' && path === '/health') return reply(res, 200, { fixture: true });
      if (req.method === 'POST' && path === '/auth/introspect') return reply(res, 200,
        { active: Boolean(roles[body.token]), userId: body.token, roles: roles[body.token] || [] });
      if (req.method === 'POST' && path === '/__test/scenarios') return reply(res, 201, scenario(body));
      if (req.method === 'PATCH' && path.startsWith('/__test/scenarios/')) {
        const s = scenarios.get(path.split('/').at(-1));
        if (!s) return error(res, 404, 'NOT_FOUND'); update(s, body); return reply(res, 200, s);
      }
      if (req.method === 'PUT' && path === '/__test/event-sink') {
        sinkAvailable = body.available; return reply(res, 200, { available: sinkAvailable });
      }
      if (req.method === 'GET' && path === '/__test/events') return reply(res, 200,
        [...events.values()].filter(e => !url.searchParams.has('gameId') || e.data.gameId === url.searchParams.get('gameId')));
      if (req.method === 'GET' && path === '/__test/images') return reply(res, 200,
        [...images.values()].filter(i => i.gameId === url.searchParams.get('gameId')));
      if (req.method === 'POST' && path === '/events') {
        if (!sinkAvailable) return error(res, 503, 'SINK_UNAVAILABLE');
        if (!body.eventId || !body.type || !body.data || !body.occurredAt || !body.aggregateId)
          return error(res, 422, 'INVALID_EVENT');
        const prior = events.get(body.eventId);
        if (prior && JSON.stringify(prior) !== JSON.stringify(body)) return error(res, 409, 'EVENT_ID_REUSED');
        events.set(body.eventId, body); return reply(res, 202, { accepted: true });
      }
      if (req.method === 'POST' && path === '/blank-verifications') {
        const b = blanks.get(body.qrToken); return b ? reply(res, 200, b) : error(res, 422, 'INVALID_QR');
      }
      if (req.method === 'POST' && path === '/images') {
        const b = [...blanks.values()].find(b => b.id === body.blankId);
        if (!b) return error(res, 422, 'UNKNOWN_BLANK');
        if (!scenarios.get(b.gameId).options.storageAvailable) return error(res, 503, 'STORAGE_UNAVAILABLE');
        if (!body.dataBase64 || !body.receiptId) return error(res, 422, 'INVALID_IMAGE');
        const id = body.receiptId, imageRef = `http://${req.headers.host}/images/${id}`;
        images.set(id, { id, gameId: b.gameId, ...body, imageRef }); return reply(res, 201, { imageRef });
      }
      if (req.method === 'GET' && path.startsWith('/images/')) {
        const img = images.get(path.split('/').at(-1)); if (!img) return error(res, 404, 'NOT_FOUND');
        res.writeHead(200, { 'content-type': img.mediaType }); return res.end(Buffer.from(img.dataBase64, 'base64'));
      }
      if (req.method === 'GET' && path.startsWith('/material-templates/')) {
        const templateKey = decodeURIComponent(path.slice('/material-templates/'.length));
        if (![...scenarios.values()].some(s => s.game.materialSettings.templateKey === templateKey))
          return error(res, 404, 'TEMPLATE_NOT_FOUND');
        return reply(res, 200, { key: templateKey, format: 'declarative-v1',
          answerSheet: [{ kind: 'field', field: 'heading' }, { kind: 'field', field: 'taskNumber' }, { kind: 'qr' }, { kind: 'answer-area' }],
          taskSheet: [{ kind: 'field', field: 'taskNumber' }, { kind: 'field', field: 'statement' }]
        });
      }
      if (req.method === 'GET') {
        const [collection, id] = path.slice(1).split('/');
        const value = collection === 'games' ? scenarios.get(id)?.game
          : collection === 'teams' ? teams.get(id) : collection === 'tasks' ? tasks.get(id)
          : collection === 'receipts' ? receipts.get(id) : null;
        if (value) return reply(res, 200, value);
      }
      return error(res, 404, 'NOT_FOUND');
    } catch { return error(res, 400, 'BAD_JSON'); }
  });
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const port = Number(process.env.FIXTURE_PORT || 19100);
  createFixtureServer().listen(port, '127.0.0.1', () => console.log(`External test fixtures: http://127.0.0.1:${port}`));
}
