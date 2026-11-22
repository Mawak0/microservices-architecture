import { useState } from 'react';
import type { Command } from '../api';
import type { Workstation } from '../app';
import { message } from '../messages';
import { filterUrl, useQuery } from '../query';
import type { Page } from '../types';
import { Subjects } from '../observer/lookup';
import type { Retake } from '../observer/scan-workplace';
import { Photo } from './photo';
import { opposite, verdictText, type Check } from './work';

interface Quota { remaining: number; limit: number }
interface Counts { answers: number; appeals: number }

export function Administration({ game, api, online, queue, jobs }: Workstation) {
  const [team, setTeam] = useState('');
  const [task, setTask] = useState('');
  const [checkId, setCheckId] = useState('');
  const [offset, setOffset] = useState(0);
  const [reason, setReason] = useState('');
  const [limit, setLimit] = useState('');
  const [problem, setProblem] = useState('');
  const [version, setVersion] = useState(1);
  const [working, setWorking] = useState(false);
  const key = { gameId: game.id };
  const path = filterUrl('/checks/search', { ...key, teamId: team, taskId: task, limit: 20, offset });
  const checks = useQuery(signal => api.read<Page<Check>>('review', path, signal), [api, path, jobs, online]);
  const retakes = useQuery(signal => api.read<Page<Retake>>('review', filterUrl('/retakes', key), signal), [api, game.id, online, jobs]);
  const quota = useQuery(signal => api.read<Quota>('review', '/appeal-quotas/' + game.id, signal), [api, game.id, online, jobs]);
  const counts = useQuery(signal => api.read<Counts>('review', filterUrl('/review-queues', key), signal), [api, game.id, online, jobs]);
  const selected = useQuery<Check | undefined>(signal => checkId
    ? api.read('review', '/checks/' + encodeURIComponent(checkId), signal) : Promise.resolve(undefined), [api, checkId, jobs]);
  const check = selected.data?.id === checkId ? selected.data : undefined;
  const photoKey = check?.id + ':' + version;
  const photo = useQuery(async signal => {
    if (!check || version > (check.imageVersion || 1)) return;
    const blob = await api.artifact('intake', '/receipts/' + encodeURIComponent(check.receiptId) + '/image/versions/' + version, signal);
    return { key: photoKey, blob };
  }, [api, photoKey, check?.imageVersion]);

  async function send(label: string, command: Command, text: string) {
    if (working || !window.confirm(text)) return;
    setWorking(true);
    setProblem('');
    try {
      await queue.enqueue(game.id, label, command);
      setReason('');
      if (online) void queue.flush().catch(error => setProblem(message(error)));
    } catch (error) { setProblem(message(error)); }
    finally { setWorking(false); }
  }

  function openCheck(id: string) {
    setCheckId(id);
    setVersion(1);
    setReason('');
  }

  return <div className="stack">
    <h1>Администрирование проверки</h1>
    {problem && <p role="alert">{problem}</p>}
    <section className="panel stack">
      <h2>Очереди и квота</h2>
      <p>Ответы {counts.data?.answers ?? '—'} · апелляции {counts.data?.appeals ?? '—'} · остаток квоты {quota.data?.remaining ?? '—'}</p>
      <form className="row" onSubmit={e => {
        e.preventDefault();
        const value = Number(limit);
        if (!limit || !Number.isSafeInteger(value) || value < 0) return;
        void send('Лимит апелляций', { service: 'review', method: 'PUT', path: '/appeal-quotas/' + game.id, body: { limit: value } },
          'Установить лимит апелляций ' + value + '?');
      }}><label>Новый лимит<input required type="number" min="0" max="2147483647" value={limit}
        placeholder={String(quota.data?.limit ?? '')} onChange={e => setLimit(e.target.value)}/></label>
        <button disabled={working}>Установить</button>
      </form>
    </section>
    <div className="columns">
      <section className="panel stack">
        <h2>Запросы пересъёмки</h2>
        <ul className="records">{retakes.data?.items.map(item => <li key={item.id}>
          <span>Задача {item.taskNumber} · {item.approved ? 'Разрешено' : 'Ожидает снимка'}</span>
          <button className="quiet" onClick={() => openCheck(item.checkId)}>Открыть дело</button>
          {!item.approved && <button className="quiet" disabled={!reason.trim() || working} onClick={() => {
            void send('Разрешение на пересъёмку', { service: 'review', body: { reason: reason.trim() },
              path: '/checks/' + item.checkId + '/retakes/' + item.id + '/approve' }, 'Разрешить пересъёмку этого бланка?');
          }}>Разрешить пересъёмку</button>}
        </li>)}</ul>
        <h2>Дела проверки</h2>
        <div className="row"><Subjects game={game} team={team} task={task}
          setTeam={value => { setTeam(value); setOffset(0); }} setTask={value => { setTask(value); setOffset(0); }}/></div>
        <ul className="records">{checks.data?.items.map(item => <li key={item.id}>
          <span>Задача {item.taskNumber} · {verdictText(item.verdict)}</span>
          <button className="quiet" onClick={() => openCheck(item.id)}>Открыть задачу {item.taskNumber}</button>
        </li>)}</ul>
        <div className="row">
          {offset > 0 && <button className="quiet" onClick={() => setOffset(offset - 20)}>Назад</button>}
          {checks.data?.nextOffset != null && <button className="quiet" onClick={() => setOffset(checks.data!.nextOffset!)}>Дальше</button>}
        </div>
      </section>
      <section className="panel stack">
        <h2>Решение администратора</h2>
        <label>Причина<textarea value={reason} onChange={e => setReason(e.target.value)}/></label>
        {selected.error != null && <p role="alert">{message(selected.error)}</p>}
        {check && <>
          <p>Задача {check.taskNumber} · ревизия {check.revision}</p>
          <label>Версия снимка<select value={version} onChange={e => setVersion(Number(e.target.value))}>
            {Array.from({ length: check.imageVersion || 1 }, (_, index) => <option key={index} value={index + 1}>{index + 1}</option>)}
          </select></label>
          {photo.error == null && <Photo key={photoKey} blob={photo.data?.key === photoKey ? photo.data.blob : undefined} title={'Версия ' + version}/>}
          <ul className="records">{check.history?.map((item, index) => <li key={index}>
            <span>{verdictText(item.verdict)} · {item.actorId}</span><time>{item.recordedAt}</time>{item.reason}
          </li>)}</ul>
          <div className="row">
            <button disabled={!reason.trim() || !check.verdict || working} onClick={() => void send('Административное исправление', {
              service: 'review', path: '/checks/' + check.id + '/corrections',
              body: { revision: check.revision, reason: reason.trim(), verdict: opposite(check.verdict) },
            }, 'Исправить вердикт в выбранном деле?')}>Исправить вердикт</button>
            <button className="quiet" disabled={working || check.status === 'awaiting-retake'} onClick={() =>
              void send('Запрос пересъёмки', { service: 'review', path: '/checks/' + check.id + '/retakes',
                body: { revision: check.revision } }, 'Запросить пересъёмку выбранного бланка?')
            }>Запросить пересъёмку</button>
          </div>
        </>}
      </section>
    </div>
  </div>;
}
