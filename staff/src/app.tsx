import { type ComponentType, useEffect, useMemo, useState } from 'react';
import { ApiError, getDirectory, getSession, StaffApi } from './api';
import { message } from './messages';
import { OutboxStore } from './outbox-store';
import { OperationQueue, type Job } from './queue';
import { can, type Actor, type Directory, type Game } from './types';

export interface Workstation {
  actor: Actor;
  game: Game;
  api: StaffApi;
  queue: OperationQueue;
  jobs: Job[];
  online: boolean;
  setDirty(value: boolean): void;
}

export interface Area {
  id: string;
  title: string;
  role: string;
  view: ComponentType<Workstation>;
}

interface Session { actor: Actor; directory: Directory }

export function StaffApp({ areas }: { areas: Area[] }) {
  const [session, setSession] = useState<Session>();
  const [state, setState] = useState<'loading' | 'login' | 'error'>('loading');

  async function open() {
    setState('loading');
    try {
      const actor = await getSession();
      const directory = await getDirectory();
      setSession({ actor, directory });
    } catch (error) {
      setState(error instanceof ApiError && error.status === 401 ? 'login' : 'error');
    }
  }

  useEffect(() => { void open(); }, []);
  if (session) return <Cabinet key={session.actor.id} session={session} areas={areas}/>;
  return <main className="welcome">
    <h1>Кабинет олимпиады</h1>
    {state === 'loading' && <p role="status">Загрузка кабинета…</p>}
    {state === 'login' && <a className="button" href="/auth/login">Войти</a>}
    {state === 'error' && <>
      <p role="alert">Кабинет временно недоступен.</p>
      <button onClick={() => void open()}>Повторить</button>
    </>}
  </main>;
}

const labels: Record<Job['state'], string> = {
  queued: 'На устройстве',
  accepted: 'Принято в обработку',
  done: 'Завершено',
  conflict: 'Требует внимания',
};

function Cabinet({ session, areas }: { session: Session; areas: Area[] }) {
  const { actor, directory } = session;
  const api = useMemo(() => new StaffApi(actor.id), [actor.id]);
  const queue = useMemo(() => new OperationQueue(api, new OutboxStore()), [api]);
  const [gameId, choose] = useState(() => sessionStorage.getItem('staff-game:' + actor.id) ?? '');
  const [section, open] = useState('');
  const [jobs, setJobs] = useState<Job[]>([]);
  const [dirty, setDirty] = useState(false);
  const [online, setOnline] = useState(navigator.onLine);
  const [failure, setFailure] = useState<string>();
  const [sessionExpired, setSessionExpired] = useState(false);

  const visible = areas.filter(area => can(actor, area.role));
  const active = visible.find(area => area.id === section) ?? visible[0];
  const games = directory.tournaments.flatMap(tournament => tournament.games);
  const game = games.find(item => item.id === gameId);
  const gameApi = useMemo(() => api.forGame(gameId), [api, gameId]);

  useEffect(() => {
    let closed = false;
    async function update() {
      try {
        const latest = await queue.list();
        if (!closed) setJobs(latest);
      } catch {
        if (!closed) setFailure('Хранилище браузера недоступно.');
      }
    }
    function tick() {
      if (navigator.onLine) void queue.flush().catch(() => setFailure('Очередь не удалось сохранить.'));
      void update();
    }
    function connected() { setOnline(navigator.onLine); tick(); }
    async function checkSession() {
      try {
        const latest = await getSession();
        if (!closed && (latest.id !== actor.id || latest.roles.join() !== actor.roles.join())) setSessionExpired(true);
      } catch (error) {
        if (!closed && error instanceof ApiError && error.status === 401) setSessionExpired(true);
      }
    }
    queue.changed.addEventListener('change', update);
    window.addEventListener('online', connected);
    window.addEventListener('offline', connected);
    window.addEventListener('focus', checkSession);
    const interval = window.setInterval(tick, 2000);
    tick();
    return () => {
      closed = true;
      clearInterval(interval);
      queue.changed.removeEventListener('change', update);
      window.removeEventListener('online', connected);
      window.removeEventListener('offline', connected);
      window.removeEventListener('focus', checkSession);
    };
  }, [actor, queue]);

  function changeGame(id: string) {
    const pending = jobs.some(job => job.gameId === gameId && ['queued', 'accepted'].includes(job.state));
    if ((dirty || pending) && !window.confirm('Есть незавершённая работа. Сменить игру?')) return;
    setDirty(false);
    sessionStorage.setItem('staff-game:' + actor.id, id);
    choose(id);
  }

  async function logout() {
    if (dirty && !window.confirm('У вас открыт несохранённый снимок. Выйти?')) return;
    try {
      const result = await fetch('/auth/logout', { method: 'POST' });
      if (!result.ok) throw new Error();
      window.location.assign('/');
    } catch {
      setFailure('Выход недоступен. Проверьте подключение.');
    }
  }

  return <>
    <header className="topbar">
      <a className="brand" href="/">Кабинет олимпиады</a>
      <label className="game-picker">Игра
        <select value={game?.id ?? ''} onChange={e => changeGame(e.target.value)}>
          <option value="">Выберите игру</option>
          {directory.tournaments.map(tournament => <optgroup key={tournament.id} label={tournament.name}>
            {tournament.games.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}
          </optgroup>)}
        </select>
      </label>
      <span className="status" role="status">{online ? 'На связи' : 'Нет связи'}</span>
      <button className="quiet" onClick={() => void logout()}>Выйти</button>
    </header>
    <div className="layout">
      <nav aria-label="Рабочие места">
        {visible.map(area => <button key={area.id} onClick={() => open(area.id)}
          aria-current={active?.id === area.id ? 'page' : undefined}>{area.title}</button>)}
      </nav>
      <main className="content">
        {failure && <p role="alert">{failure}</p>}
        {sessionExpired ? <p role="alert">Сеанс изменился. <a href="/">Открыть кабинет заново</a></p>
          : game && active ? <active.view key={game.id + ':' + active.id} actor={actor}
              game={game} api={gameApi} queue={queue} jobs={jobs.filter(job => job.gameId === game.id)}
              online={online} setDirty={setDirty}/>
            : <h1>{game ? 'Рабочие места' : 'Выберите игру'}</h1>}
        <details className="outbox">
          <summary>Отправка · {jobs.filter(job => job.state === 'queued' || job.state === 'accepted').length}</summary>
          {jobs.length === 0 ? <p>Очередь пуста.</p> : <ul>
            {jobs.slice().reverse().map(job => <li key={job.key}>
              <span>{job.label} · {labels[job.state]} {job.error && '· ' + message(job.error)}</span>
              {(job.state === 'done' || job.state === 'conflict') && <button className="quiet"
                onClick={() => {
                  if (job.state === 'done' || window.confirm('Удалить сохранённое действие?'))
                    void queue.remove(job);
                }}>Убрать</button>}
            </li>)}
          </ul>}
        </details>
      </main>
    </div>
  </>;
}
