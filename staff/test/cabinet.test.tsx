import 'fake-indexeddb/auto';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { StaffApp, type Area } from '../src/app';
import { observerAreas } from '../src/observer';
import { OutboxStore } from '../src/outbox-store';
import { reviewAreas } from '../src/reviewer';

const directory = { tournaments: [{ id: 't', name: 'Финал', games: [
  { id: 'g', name: 'Бонусы', tasks: [], teams: [] }, { id: 'd', name: 'Домино', tasks: [], teams: [] },
] }] };
const areas: Area[] = [
  { id: 'c', role: 'checker', title: 'Проверка', view: ({ game, setDirty }) =>
    <button onClick={() => setDirty(true)}>{game.name + ' · проверка'}</button> },
  { id: 'o', role: 'observer', title: 'Приём', view: () => <p>Бланки</p> },
];
const answer = (data: unknown, status = 200) => new Response(JSON.stringify(data), { status });
afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.restoreAllMocks(); sessionStorage.clear(); });

it('shows only authorized rooms and guards an unfinished action on game switch', async () => {
  vi.stubGlobal('fetch', vi.fn(async (path: string) =>
    path === '/api/session' ? answer({ id: 'checker', roles: ['checker'] }) : answer(directory)));
  const confirm = vi.spyOn(window, 'confirm').mockReturnValueOnce(false).mockReturnValueOnce(true);
  render(<StaffApp areas={areas}/>);
  const picker = await screen.findByLabelText('Игра');
  expect(screen.getByRole('heading', { name: 'Проверка' })).toBeDefined();
  expect(screen.getByText('Выберите игру в верхней панели.')).toBeDefined();
  expect(screen.queryByRole('button', { name: 'Приём' })).toBeNull();
  fireEvent.change(picker, { target: { value: 'g' } });
  fireEvent.click(screen.getByText('Бонусы · проверка'));
  fireEvent.change(picker, { target: { value: 'd' } });
  expect(screen.queryByText('Домино · проверка')).toBeNull();
  fireEvent.change(picker, { target: { value: 'd' } });
  expect(screen.getByText('Домино · проверка')).toBeDefined();
  expect(confirm).toHaveBeenCalledTimes(2);
  fireEvent(window, new Event('offline'));
  expect(screen.getByText('Онлайн')).toBeDefined();
});

it('opens each administrator section immediately when the catalog contains one game', async () => {
  sessionStorage.setItem('staff-game:admin', 'removed-game');
  const onlyGame = { tournaments: [{ ...directory.tournaments[0], games: [directory.tournaments[0].games[0]] }] };
  vi.stubGlobal('fetch', vi.fn(async (path: string) => {
    if (path === '/api/session') return answer({ id: 'admin', roles: ['admin'] });
    if (path === '/api/context') return answer(onlyGame);
    return answer({ items: [], nextOffset: null });
  }));

  render(<StaffApp areas={[...observerAreas, ...reviewAreas]}/>);
  expect((await screen.findByLabelText<HTMLSelectElement>('Игра')).value).toBe('g');
  const sections = [
    ['Печать', 'Печатные материалы'],
    ['Приём бланков', 'Приём бланков'],
    ['Проверка ответов', 'Проверка ответов'],
    ['Администрирование', 'Администрирование проверки'],
  ];
  for (const [tab, heading] of sections) {
    const button = screen.getByRole('button', { name: tab, exact: true });
    fireEvent.click(button);
    expect(button.getAttribute('aria-current')).toBe('page');
    expect(screen.getByRole('heading', { name: heading, exact: true })).toBeDefined();
    if (tab === 'Печать') {
      expect(await screen.findByText('Бланки по этим фильтрам не найдены.')).toBeDefined();
      expect(screen.getByText('Заданий печати пока нет.')).toBeDefined();
    }
  }
});

it('returns to login and does not show another profile’s queue', async () => {
  const store = new OutboxStore();
  await store.put({ actorId: 'other', gameId: 'g', key: 'foreign', path: '/receipts', service: 'intake',
    state: 'conflict', label: 'Чужой бланк', createdAt: 0 });
  await store.put({ actorId: 'self', gameId: 'g', key: 'mine', path: '/receipts', service: 'intake',
    state: 'conflict', label: 'Мой бланк', error: 'QR_UNREADABLE', createdAt: 0 });
  const sender = vi.fn(async (path: string) => path === '/api/session'
    ? answer({ id: 'self', roles: ['observer', 'checker'] }) : answer(directory));
  sender.mockResolvedValueOnce(answer({ code: 'UNAUTHENTICATED' }, 401));
  vi.stubGlobal('fetch', sender);
  const { unmount } = render(<StaffApp areas={areas}/>);
  expect((await screen.findByText('Войти')).getAttribute('href')).toBe('/auth/login');
  unmount();
  render(<StaffApp areas={areas}/>);
  expect(await screen.findByText(/Мой бланк/)).toBeDefined();
  expect(screen.queryByText(/Чужой бланк/)).toBeNull();
  vi.spyOn(window, 'confirm').mockReturnValue(true);
  fireEvent.click(screen.getByRole('button', { name: 'Убрать', hidden: true }));
  await waitFor(() => expect(screen.queryByText(/Мой бланк/)).toBeNull());
});

it('offers recovery when the catalog could not be read', async () => {
  const sender = vi.fn(async (path: string) => path === '/api/session'
    ? answer({ id: 'a', roles: [] }) : answer(directory));
  sender.mockRejectedValueOnce(new TypeError('offline'));
  vi.stubGlobal('fetch', sender);
  render(<StaffApp areas={[]}/>);
  expect(await screen.findByRole('alert')).toBeDefined();
  fireEvent.click(screen.getByRole('button', { name: 'Повторить' }));
  expect(await screen.findByLabelText('Игра')).toBeDefined();
});
