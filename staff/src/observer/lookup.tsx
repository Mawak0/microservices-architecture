import { useState } from 'react';
import type { Workstation } from '../app';
import { message } from '../messages';
import { filterUrl, useQuery } from '../query';
import type { Game, Page } from '../types';

interface Receipt { id: string; blankId: string; teamId: string; taskNumber: string; acceptedAt: string }
interface ReviewCase { id: string; receiptId: string; teamId: string; taskNumber: string; status: string }
interface AppealPermission { allowed: boolean; remaining: number; code?: string }

export function Subjects({ game, team, task, setTeam, setTask }: { game: Game; team: string; task: string;
  setTeam(team: string): void; setTask(task: string): void }) {
  return <>
    <label>Команда<select value={team} onChange={e => setTeam(e.target.value)}>
      <option value="">Все команды</option>
      {game.teams.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}
    </select></label>
    <label>Задача<select value={task} onChange={e => setTask(e.target.value)}>
      <option value="">Все задачи</option>
      {game.tasks.map(item => <option key={item.id} value={item.id}>{item.number}</option>)}
    </select></label>
  </>;
}

export function Lookup({ game, api, queue, online, jobs }: Workstation) {
  const [team, setTeam] = useState('');
  const [task, setTask] = useState('');
  const [blank, setBlank] = useState('');
  const [offset, setOffset] = useState(0);
  const [caseItem, openCase] = useState<ReviewCase>();
  const [reason, setReason] = useState('');
  const [problem, setProblem] = useState('');
  const [saving, setSaving] = useState(false);
  const path = filterUrl('/receipts', { gameId: game.id, teamId: team, taskId: task, blankId: blank.trim(), offset, limit: 20 });
  const list = useQuery(signal => api.read<Page<Receipt>>('intake', path, signal), [api, path, online, jobs]);
  const permission = useQuery<AppealPermission | undefined>(signal => caseItem
    ? api.read('review', '/checks/' + encodeURIComponent(caseItem.id) + '/appeal-availability', signal)
    : Promise.resolve(undefined), [api, caseItem, online]);

  async function findCase(receipt: Receipt) {
    openCase(undefined);
    setProblem('');
    try {
      const result = await api.read<Page<ReviewCase>>('review',
        filterUrl('/checks/search', { gameId: game.id, receiptId: receipt.id }));
      if (result.items.length === 0) setProblem('Бланк ещё передаётся в проверку.');
      else openCase(result.items[0]);
    } catch (error) { setProblem(message(error)); }
  }

  async function appeal() {
    if (!caseItem || !reason.trim() || saving || !permission.data?.allowed ||
        !window.confirm('Подать апелляцию и использовать одно обращение из общей квоты?')) return;
    setSaving(true);
    try {
      await queue.enqueue(game.id, 'Апелляция · задача ' + caseItem.taskNumber, {
        service: 'review', path: '/appeals', body: { checkId: caseItem.id, reason: reason.trim() },
      });
      openCase(undefined);
      setReason('');
      if (online) void queue.flush().catch(error => setProblem(message(error)));
    } catch (error) { setProblem(message(error)); }
    finally { setSaving(false); }
  }

  return <section className="panel stack">
    <h2>Журнал приёма</h2>
    <div className="row">
      <Subjects game={game} team={team} task={task} setTeam={value => { setTeam(value); setOffset(0); }}
        setTask={value => { setTask(value); setOffset(0); }}/>
      <label>ID бланка<input value={blank} onChange={e => { setBlank(e.target.value); setOffset(0); }}/></label>
      <button className="quiet" onClick={list.reload}>Обновить журнал</button>
    </div>
    {problem && <p role="alert">{problem}</p>}
    {list.error != null && <p role="status">Журнал не обновился. Проверьте связь.</p>}
    {list.data?.items.length === 0 ? <p>Нет принятых бланков.</p> : <ul className="records">
      {list.data?.items.map(item => <li key={item.id}>
        <span>{game.teams.find(t => t.id === item.teamId)?.name ?? item.teamId} · задача {item.taskNumber}</span>
        <time dateTime={item.acceptedAt}>{new Date(item.acceptedAt).toLocaleTimeString('ru-RU')}</time>
        <button className="quiet" onClick={() => void findCase(item)}>Апелляция</button>
      </li>)}
    </ul>}
    <div className="row">
      {offset > 0 && <button className="quiet" onClick={() => setOffset(offset - 20)}>Назад</button>}
      {list.data?.nextOffset != null && <button className="quiet" onClick={() => setOffset(list.data!.nextOffset!)}>Дальше</button>}
    </div>
    {caseItem && <div className="stack">
      <h3>Апелляция · задача {caseItem.taskNumber}</h3>
      {permission.data && <p>Доступно обращений в игре — {permission.data.remaining}</p>}
      {permission.error != null && <p role="alert">{message(permission.error)}</p>}
      {permission.data?.code && <p role="status">{message(permission.data.code)}</p>}
      <form className="stack" onSubmit={e => { e.preventDefault(); void appeal(); }}>
        <label>Причина<textarea required value={reason} onChange={e => setReason(e.target.value)}/></label>
        <div className="row">
          <button disabled={!permission.data?.allowed || saving}>Подать апелляцию</button>
          <button type="button" className="quiet" onClick={() => { openCase(undefined); setReason(''); }}>Закрыть</button>
        </div>
      </form>
    </div>}
  </section>;
}
