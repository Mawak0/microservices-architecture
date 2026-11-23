import { useState } from 'react';
import type { Workstation } from '../app';
import { message } from '../messages';
import { filterUrl, useQuery } from '../query';
import { can, type Page } from '../types';
import { Subjects } from './lookup';

interface Blank { id: string; number: number; taskNumber: string }
interface Kit { id: string; blankIds: string[] }
interface PrintJob { id: string; status: string }

export function download(blob: Blob, filename: string) {
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.download = filename;
  link.href = url;
  link.click();
  window.setTimeout(() => URL.revokeObjectURL(url), 5000);
}

export function Printing({ game, actor, api, queue, jobs, online }: Workstation) {
  const [team, setTeam] = useState('');
  const [task, setTask] = useState('');
  const [selected, select] = useState<string[]>([]);
  const [taskCards, showCards] = useState(true);
  const [offset, setOffset] = useState(0);
  const [problem, setProblem] = useState('');
  const [working, setWorking] = useState(false);
  const [count, setCount] = useState(1);
  const [modifier, setModifier] = useState('');
  const list = useQuery(signal => api.read<Page<PrintJob>>('materials',
    filterUrl('/print-jobs', { gameId: game.id, teamId: team }), signal), [api, game.id, team, jobs, online]);
  const blanks = useQuery(signal => api.read<Page<Blank>>('materials',
    filterUrl('/blanks', { gameId: game.id, teamId: team, taskId: task, offset, limit: 25 }), signal),
  [api, game.id, team, task, offset, jobs, online]);

  async function enqueue(label: string, path: string, body: object) {
    setProblem('');
    await queue.enqueue(game.id, label, { service: 'materials', path, body });
    if (online) void queue.flush().catch(error => setProblem(message(error)));
  }

  async function safely(work: () => Promise<void>) {
    if (working) return;
    setWorking(true);
    try { await work(); }
    catch (error) { setProblem(message(error)); }
    finally { setWorking(false); }
  }

  function prepare(blankIds: string[]) {
    const taskIds = taskCards ? (task ? [task] : game.tasks.map(item => item.id)) : [];
    return enqueue('Печатные материалы', '/print-jobs', { gameId: game.id, teamId: team || null, blankIds, taskIds });
  }

  async function entireKit() {
    const kits = await api.read<Page<Kit>>('materials', filterUrl('/kits', { gameId: game.id, teamId: team }));
    if (!kits.items.length) { setProblem('Комплект команды ещё не выпущен.'); return; }
    const kit = await api.read<Kit>('materials', '/kits/' + encodeURIComponent(kits.items[0].id));
    await enqueue('Комплект команды', '/print-jobs', { gameId: game.id, teamId: team,
      blankIds: kit.blankIds, taskIds: game.tasks.map(item => item.id) });
  }

  async function issue() {
    if (!window.confirm('Выпустить ' + count + ' новых бланков для выбранной команды и задачи?')) return;
    const selectedModifier = modifier ? { kind: modifier, value: modifier === 'add' ? 3 : 2 } : null;
    await enqueue('Дополнительные бланки', '/blanks', { gameId: game.id, teamId: team,
      taskId: task, count, modifier: selectedModifier });
  }

  return <div className="columns">
    <section className="panel stack">
      <h1>Печатные материалы</h1>
      <div className="row">
        <Subjects game={game} team={team} task={task}
          setTeam={value => { setTeam(value); setOffset(0); select([]); }}
          setTask={value => { setTask(value); setOffset(0); select([]); }}/>
      </div>
      {problem && <p role="alert">{problem}</p>}
      {blanks.error != null && <p role="status">Список бланков недоступен.</p>}
      <fieldset>
        <legend>Бланки в PDF</legend>
        {blanks.data?.items.length === 0 && blanks.error == null && <p>Бланки по этим фильтрам не найдены.</p>}
        {(blanks.data?.items ?? []).map(blank => <label key={blank.id}>
          <input type="checkbox" checked={selected.includes(blank.id)}
            onChange={e => select(e.target.checked ? [...selected, blank.id] : selected.filter(id => id !== blank.id))}/>
          № {blank.number} · задача {blank.taskNumber}
        </label>)}
      </fieldset>
      <label><input type="checkbox" checked={taskCards} onChange={e => showCards(e.target.checked)}/>Добавить условия задач</label>
      <div className="row">
        {offset > 0 && <button className="quiet" onClick={() => setOffset(offset - 25)}>Предыдущие бланки</button>}
        {blanks.data?.nextOffset != null && <button className="quiet" onClick={() => setOffset(blanks.data!.nextOffset!)}>Следующие бланки</button>}
        <button disabled={working || !selected.length && !taskCards} onClick={() => void safely(() => prepare(selected))}>Подготовить PDF</button>
        <button className="quiet" disabled={working || !team || !online} onClick={() => void safely(entireKit)}>Весь комплект</button>
      </div>
      {can(actor, 'admin') && <fieldset className="stack">
        <legend>Выпуск бланков</legend>
        <button className="quiet" disabled={working || !team} onClick={() => {
          if (window.confirm('Подготовить комплект выбранной команды?'))
            void safely(() => enqueue('Комплект команды', '/kits', { gameId: game.id, teamId: team }));
        }}>Создать комплект</button>
        <label>Количество<input type="number" min={1} max={10000} value={count} onChange={e => setCount(e.target.valueAsNumber || 1)}/></label>
        {game.capabilities?.modifiersAllowed && <label>Модификатор<select value={modifier} onChange={e => setModifier(e.target.value)}>
          <option value="">Без модификатора</option><option value="add">+3</option><option value="multiply">×2</option>
        </select></label>}
        <button disabled={working || !team || !task || count < 1 || count > 10000} onClick={() => void safely(issue)}>Выпустить дополнительные</button>
      </fieldset>}
    </section>
    <section className="panel">
      <h2>Задания печати</h2>
      <button className="quiet" onClick={list.reload}>Обновить список</button>
      {list.error != null && <p role="status">Список печати недоступен.</p>}
      {list.data?.items.length === 0 && list.error == null && <p>Заданий печати пока нет.</p>}
      <ul className="records">{list.data?.items.map(job => <li key={job.id}>
        <span>{job.id.slice(0, 8)} · {job.status === 'completed' ? 'PDF готов' : job.status === 'failed' ? 'Ошибка подготовки' : 'Готовится'}</span>
        {job.status === 'completed' && <button className="quiet" disabled={working || !online} onClick={() => void safely(async () => {
          const pdf = await api.artifact('materials', '/print-jobs/' + encodeURIComponent(job.id) + '/artifact');
          download(pdf, 'olympiad-' + job.id + '.pdf');
        })}>Скачать PDF</button>}
      </li>)}</ul>
    </section>
  </div>;
}
