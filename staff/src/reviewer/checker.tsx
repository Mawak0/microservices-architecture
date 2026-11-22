import { useEffect, useRef, useState } from 'react';
import type { Command } from '../api';
import type { Workstation } from '../app';
import { message } from '../messages';
import { filterUrl, useQuery } from '../query';
import { Photo } from './photo';
import { nextWork, opposite, renewOrRelease, validLease, verdictText, withPhoto, type Activity, type Lane, type Verdict, type Work } from './work';

interface Counts { answers: number; appeals: number }

export function Checker({ api, game, queue, online, jobs }: Workstation) {
  const [work, show] = useState<Work>();
  const [lane, setLane] = useState<Lane>('checks');
  const [paused, pause] = useState(false);
  const [busy, setBusy] = useState(false);
  const [reason, setReason] = useState('');
  const [disagreement, setDisagreement] = useState(false);
  const [status, setStatus] = useState('');
  const [error, fail] = useState('');
  const [recentOffset, setRecentOffset] = useState(0);
  const action = useRef(false);
  const mounted = useRef(true);
  const next = useRef<Promise<Work | undefined> | undefined>(undefined);

  const activityPath = filterUrl('/checks/activity', { gameId: game.id, offset: recentOffset, limit: 12 });
  const activity = useQuery(signal => api.read<Activity>('review', activityPath, signal), [api, activityPath, online, jobs]);
  const counts = useQuery(signal => api.read<Counts>('review', filterUrl('/review-queues', { gameId: game.id }), signal),
    [api, game.id, online, jobs]);

  useEffect(() => {
    mounted.current = true;
    return () => { mounted.current = false; next.current = undefined; };
  }, []);

  useEffect(() => {
    if (work?.lane === 'checks' && validLease(work) && online && !paused && !next.current) {
      next.current = nextWork(api, game.id, work.lane, work.id).catch(() => undefined);
    }
  }, [api, game.id, work, online, paused]);

  useEffect(() => {
    if (!work || paused || !online) return;
    const timer = window.setInterval(() => {
      if (!validLease(work) || action.current) return;
      void renewOrRelease(api, work, 'renew').then(updated =>
        show(current => current?.id === updated.id ? updated : current), problem => fail(message(problem)));
    }, 20_000);
    return () => clearInterval(timer);
  }, [api, work, paused, online]);

  async function run(operation: () => Promise<void>) {
    if (action.current) return;
    action.current = true;
    setBusy(true);
    fail('');
    try { await operation(); }
    catch (problem) { if (mounted.current) fail(message(problem)); }
    finally { action.current = false; if (mounted.current) setBusy(false); }
  }

  async function pick() {
    const own = lane === 'checks' ? activity.data?.assignments.find(check =>
      check.lease && check.status === 'checking' && Date.parse(check.lease.expiresAt) > Date.now()) : undefined;
    const item = own ? await withPhoto(api, { id: own.id, lane, check: own }) : await nextWork(api, game.id, lane);
    if (mounted.current) {
      show(item);
      setStatus(item ? '' : 'Доступных заданий пока нет.');
      counts.reload();
    }
  }

  async function stage(label: string, command: Command) {
    await queue.enqueue(game.id, label, command);
    if (online) void queue.flush().catch(problem => fail(message(problem)));
    counts.reload();
    activity.reload();
  }

  function finish(verdict: Verdict | 'retake' | 'upheld' | 'corrected') {
    if (!work || paused || !validLease(work) || action.current) return;
    if (verdict !== 'retake' && !work.photo) return;
    if (work.lane === 'appeals' && verdict !== 'retake' && (!reason.trim() ||
      !window.confirm('Сохранить решение по апелляции?'))) return;
    void run(async () => {
      const token = work.check.lease!.token;
      let command: Command;
      if (verdict === 'retake') command = { service: 'review', path: '/checks/' + work.check.id + '/retakes', token,
        body: { revision: work.check.revision } };
      else if (work.lane === 'appeals') command = { service: 'review', path: '/appeals/' + work.id + '/resolve', token,
        body: { outcome: verdict === 'upheld' ? 'upheld' : 'jury-error-corrected', reason: reason.trim(),
          ...(verdict === 'upheld' ? {} : { verdict: opposite(work.check.verdict ?? work.check.originalVerdict) }) } };
      else command = { service: 'review', path: '/checks/' + work.id + '/verdict', token, body: { verdict } };
      await stage((verdict === 'retake' ? 'Пересъёмка' : work.lane === 'appeals' ? 'Решение апелляции' : 'Вердикт') +
        ' · задача ' + work.check.taskNumber, command);
      const reserved = next.current;
      next.current = undefined;
      let prepared = await reserved;
      if (online && (!prepared || !validLease(prepared)))
        prepared = await nextWork(api, game.id, work.lane, work.id).catch(() => undefined);
      if (mounted.current) {
        show(prepared && validLease(prepared) ? prepared : undefined);
        setStatus('Действие сохранено в очереди отправки.');
        setReason('');
        setDisagreement(false);
      }
    });
  }

  useEffect(() => {
    function keyboard(event: KeyboardEvent) {
      if (lane !== 'checks' || !work || busy || event.repeat || event.ctrlKey || event.altKey || event.metaKey ||
          event.target instanceof HTMLElement && (event.target.matches('input, textarea, select, button') || event.target.isContentEditable)) return;
      if (event.key === '1' || event.key === '2') {
        event.preventDefault();
        finish(event.key === '1' ? 'correct' : 'incorrect');
      }
    }
    window.addEventListener('keydown', keyboard);
    return () => window.removeEventListener('keydown', keyboard);
  });

  function changeLane(value: Lane) {
    if (work || busy) return;
    setLane(value);
    setStatus('');
    setReason('');
  }

  return <div className="stack">
    <h1>Проверка ответов</h1>
    <div className="row">
      <button className={lane === 'checks' ? '' : 'quiet'} disabled={!!work || busy} onClick={() => changeLane('checks')}>Ответы · {counts.data?.answers ?? '—'}</button>
      <button className={lane === 'appeals' ? '' : 'quiet'} disabled={!!work || busy} onClick={() => changeLane('appeals')}>Апелляции · {counts.data?.appeals ?? '—'}</button>
      {(!online || counts.error != null) && <span role="status">Счётчики могут быть устаревшими.</span>}
      <button className="quiet" disabled={!work} onClick={() => pause(value => !value)}>{paused ? 'Продолжить проверку' : 'Пауза'}</button>
    </div>
    {error && <p role="alert">{error}</p>}
    {status && <p role="status">{status}</p>}
    {!work ? <section className="panel"><button disabled={!online || busy} onClick={() => void run(pick)}>Взять следующую карточку</button></section> :
      <div className="columns">
        <section className="panel"><Photo key={work.check.imageVersion} blob={work.photo} title={'Бланк задачи ' + work.check.taskNumber}/>
          {!work.photo && <button className="quiet" disabled={!online || busy} onClick={() => void run(async () => show(await withPhoto(api, work)))}>Повторить загрузку</button>}
        </section>
        <section className="panel stack">
          <h2>Задача {work.check.taskNumber}</h2>
          <p>Эталон <strong>{work.check.referenceAnswer}</strong></p>
          {!validLease(work) && <p role="alert">Срок назначения истёк.</p>}
          {work.lane === 'checks' ? <div className="row">
            <button className="correct" disabled={busy || paused || !work.photo || !validLease(work)} onClick={() => finish('correct')}>Верно · 1</button>
            <button className="incorrect" disabled={busy || paused || !work.photo || !validLease(work)} onClick={() => finish('incorrect')}>Неправильно · 2</button>
          </div> : <>
            <p>Исходный вердикт <strong>{verdictText(work.check.originalVerdict)}</strong></p>
            <p>{work.reason}</p>
            <label>Обоснование<textarea value={reason} onChange={e => setReason(e.target.value)}/></label>
            <label><input type="checkbox" checked={disagreement} onChange={e => setDisagreement(e.target.checked)}/>Исправить ошибку жюри</label>
            <button disabled={!reason.trim() || !work.photo || busy || paused || !validLease(work)}
              onClick={() => finish(disagreement ? 'corrected' : 'upheld')}>Сохранить решение</button>
          </>}
          <div className="row">
            <button className="quiet" disabled={busy || paused || !validLease(work)} onClick={() => finish('retake')}>Плохое фото</button>
            <button className="quiet" disabled={busy || !online} onClick={() => void run(async () => {
              if (validLease(work)) await renewOrRelease(api, work, 'release');
              const prepared = await next.current;
              next.current = undefined;
              if (prepared && validLease(prepared)) await renewOrRelease(api, prepared, 'release');
              pause(false); show(undefined); counts.reload();
            })}>Вернуть задание</button>
          </div>
        </section>
      </div>}
    <section className="panel">
      <h2>Мои последние решения</h2>
      <ul className="records">{activity.data?.decisions.map(item => <li key={item.id}>
        <span>Задача {item.taskNumber} · {verdictText(item.verdict)}</span>
        {item.verdict && item.status === 'checked' && <button className="quiet" disabled={busy} onClick={() => void run(async () => {
          await stage('Исправление вердикта · задача ' + item.taskNumber, { service: 'review',
            path: '/checks/' + item.id + '/self-correction', body: { revision: item.revision, verdict: opposite(item.verdict) } });
        })}>Изменить на «{verdictText(opposite(item.verdict))}»</button>}
      </li>)}</ul>
      {recentOffset > 0 && <button className="quiet" onClick={() => setRecentOffset(recentOffset - 12)}>Назад</button>}
      {activity.data?.nextOffset != null && <button className="quiet" onClick={() => setRecentOffset(activity.data!.nextOffset!)}>Дальше</button>}
    </section>
  </div>;
}
