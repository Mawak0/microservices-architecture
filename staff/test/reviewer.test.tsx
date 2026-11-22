import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { ApiError } from '../src/api';
import type { Workstation } from '../src/app';
import { Administration } from '../src/reviewer/admin';
import { Checker } from '../src/reviewer/checker';
import type { Job } from '../src/queue';
import { nextWork, renewOrRelease, validLease, withPhoto, type Check, type Work } from '../src/reviewer/work';
import type { StaffApi } from '../src/api';

const photo = new Blob(['form']);
const check = (sequence: number, status = 'checking'): Check => ({ id: 'check-' + sequence,
  receiptId: 'receipt-' + sequence, teamId: 't', taskNumber: String(sequence), referenceAnswer: '5',
  imageRef: '/receipts/receipt-' + sequence + '/image', imageVersion: 2, revision: 1, status,
  verdict: status === 'checked' ? 'incorrect' : undefined, originalVerdict: 'incorrect',
  lease: { token: 'token-' + sequence, expiresAt: new Date(Date.now() + 600_000).toISOString() },
  history: [{ verdict: 'incorrect', actorId: 'alice', recordedAt: '2026-11-22T12:00:00Z' }],
});
const page = (items: object[]) => ({ items, nextOffset: null });
function context(online = true) {
  let picks = 0;
  let appeal = true;
  const api = {
    read: vi.fn(async (_, path: string) => {
      if (path.startsWith('/review-queues')) return { answers: 3, appeals: 1 };
      if (path.startsWith('/checks/activity')) return { assignments: [], decisions: [check(7, 'checked')], nextOffset: null };
      if (path.startsWith('/checks/search')) return page([check(3, 'checked')]);
      if (path.startsWith('/retakes')) return page([{ id: 'request-1', checkId: 'check-3', taskNumber: '3', requiresApproval: true }]);
      if (path.startsWith('/appeal-quotas')) return { limit: 2, remaining: 1 };
      return check(3, 'checked');
    }),
    command: vi.fn(async spec => {
      if (spec.path === '/checks/next') return ++picks < 4 ? check(picks) : undefined;
      if (spec.path === '/appeals/next' && appeal) {
        appeal = false;
        return { id: 'appeal-1', check: check(3), reason: 'Жюри неверно прочитало ответ' };
      }
      return undefined;
    }),
    artifact: vi.fn(async () => photo),
  };
  const queue = { enqueue: vi.fn(async (gameId, label, command) => ({ ...command, gameId, label,
    key: 'job', actorId: 'alice', state: 'queued', createdAt: 0 } as Job)), flush: vi.fn(async () => {}) };
  const props = { actor: { id: 'alice', roles: ['admin'] }, game: { id: 'g', name: 'Бонусы',
    teams: [{ id: 't', name: 'Вектор' }], tasks: [{ id: 'task', number: '3' }] },
    api, queue, online, jobs: [], setDirty: vi.fn() } as unknown as Workstation;
  return { props, api, queue };
}
beforeEach(() => { URL.createObjectURL = vi.fn(() => 'blob:photo'); URL.revokeObjectURL = vi.fn(); });
afterEach(() => { cleanup(); vi.restoreAllMocks(); });

it('saves a single keyboard verdict and shows the prepared next card with its own lease', async () => {
  const { props, api, queue } = context();
  render(<Checker {...props}/>);
  fireEvent.click(await screen.findByText('Взять следующую карточку'));
  await screen.findByRole('img', { name: 'Бланк задачи 1' });
  await waitFor(() => expect(api.artifact).toHaveBeenCalledTimes(2));
  fireEvent.keyDown(window, { key: '1', repeat: true });
  expect(queue.enqueue).not.toHaveBeenCalled();
  fireEvent.keyDown(window, { key: '1' });
  fireEvent.keyDown(window, { key: '1' });
  await screen.findByRole('heading', { name: 'Задача 2' });
  expect(queue.enqueue).toHaveBeenCalledTimes(1);
  expect(queue.enqueue.mock.calls[0][2]).toMatchObject({
    path: '/checks/check-1/verdict', token: 'token-1', body: { verdict: 'correct' },
  });
  expect(screen.getByText('Действие сохранено в очереди отправки.')).toBeDefined();
  fireEvent.click(screen.getByText('Повернуть'));
  fireEvent.change(screen.getByLabelText('Масштаб'), { target: { value: '2' } });
  expect(screen.getByRole('img').style.transform).toContain('rotate(90deg) scale(2)');
  fireEvent.click(screen.getByText('Пауза'));
  expect((screen.getByText('Верно · 1') as HTMLButtonElement).disabled).toBe(true);
  fireEvent.click(screen.getByText('Продолжить проверку'));
  fireEvent.click(screen.getByText('Плохое фото'));
  await waitFor(() => expect(queue.enqueue).toHaveBeenCalledTimes(2));
  expect(queue.enqueue.mock.calls[1][2]).toMatchObject({ path: '/checks/check-2/retakes', token: 'token-2' });
});

it('honors the old verdict in an appeal, requires confirmation, and uses the exact appeal lease', async () => {
  const { props, queue } = context();
  vi.spyOn(window, 'confirm').mockReturnValueOnce(false).mockReturnValueOnce(true);
  render(<Checker {...props}/>);
  fireEvent.click(await screen.findByText('Апелляции · 1'));
  fireEvent.click(screen.getByText('Взять следующую карточку'));
  await screen.findByText('Жюри неверно прочитало ответ');
  expect(screen.getByText('Исходный вердикт')).toBeDefined();
  fireEvent.change(screen.getByLabelText('Обоснование'), { target: { value: 'Видно на снимке' } });
  fireEvent.click(screen.getByLabelText('Исправить ошибку жюри'));
  fireEvent.click(screen.getByText('Сохранить решение'));
  expect(queue.enqueue).not.toHaveBeenCalled();
  fireEvent.click(screen.getByText('Сохранить решение'));
  expect(queue.enqueue.mock.calls[0][2]).toMatchObject({ path: '/appeals/appeal-1/resolve', token: 'token-3',
    body: { outcome: 'jury-error-corrected', verdict: 'correct', reason: 'Видно на снимке' } });
});

it('opens the next answer after confirmation when no second assignment could be prepared', async () => {
  const { props, api, queue } = context();
  api.command.mockResolvedValueOnce(check(1)).mockResolvedValueOnce(undefined).mockResolvedValueOnce(check(2));
  const { rerender } = render(<Checker {...props}/>);
  fireEvent.click(await screen.findByText('Взять следующую карточку'));
  await screen.findByAltText('Бланк задачи 1');
  await waitFor(() => expect(api.command).toHaveBeenCalledTimes(2));
  fireEvent.keyDown(window, { key: '1' });
  await screen.findByText('Действие сохранено в очереди отправки.');
  expect(api.command).toHaveBeenCalledTimes(2);
  const queued = await queue.enqueue.mock.results[0].value;
  rerender(<Checker {...props} jobs={[{ ...queued, state: 'done' }]}/>);
  await screen.findByAltText('Бланк задачи 2');
  expect(api.command).toHaveBeenNthCalledWith(3, expect.objectContaining({ path: '/checks/next' }));
});

it('uses the current revision for a self-correction even while offline', async () => {
  const { props, queue } = context(false);
  render(<Checker {...props}/>);
  expect(screen.getByText('Счётчики могут быть устаревшими.')).toBeDefined();
  fireEvent.click(await screen.findByText('Изменить на «Верно»'));
  expect(queue.enqueue.mock.calls[0][2]).toMatchObject({ path: '/checks/check-7/self-correction',
    body: { revision: 1, verdict: 'correct' } });
  expect(queue.flush).not.toHaveBeenCalled();
});

it('releases the current and prepared assignments without issuing verdicts', async () => {
  const { props, api, queue } = context();
  render(<Checker {...props}/>);
  fireEvent.click(screen.getByText('Взять следующую карточку'));
  await screen.findByAltText('Бланк задачи 1');
  fireEvent.click(screen.getByText('Вернуть задание'));
  await screen.findByText('Взять следующую карточку');
  expect(api.command).toHaveBeenCalledWith(expect.objectContaining({ path: '/checks/check-1/release', token: 'token-1' }));
  expect(api.command).toHaveBeenCalledWith(expect.objectContaining({ path: '/checks/check-2/release', token: 'token-2' }));
  expect(queue.enqueue).not.toHaveBeenCalled();
});

it('admin inspects the requested photo version and sends only confirmed changes', async () => {
  const { props, api, queue } = context();
  vi.spyOn(window, 'confirm').mockReturnValue(true);
  render(<Administration {...props}/>);
  fireEvent.click(await screen.findByText('Открыть дело'));
  await screen.findByText('Задача 3 · ревизия 1');
  await screen.findByAltText('Версия 1');
  fireEvent.change(screen.getByLabelText('Версия снимка'), { target: { value: '2' } });
  await screen.findByAltText('Версия 2');
  expect(api.artifact).toHaveBeenCalledWith('intake', '/receipts/receipt-3/image/versions/2', expect.anything());
  fireEvent.change(screen.getByLabelText('Причина'), { target: { value: 'Фото размыто' } });
  fireEvent.click(screen.getByText('Разрешить пересъёмку'));
  await waitFor(() => expect(queue.enqueue).toHaveBeenCalledTimes(1));
  expect(queue.enqueue.mock.calls[0][2]).toMatchObject({ path: '/checks/check-3/retakes/request-1/approve' });
  await waitFor(() => expect((screen.getByText('Запросить пересъёмку') as HTMLButtonElement).disabled).toBe(false));
  fireEvent.change(screen.getByLabelText('Причина'), { target: { value: 'После сверки' } });
  fireEvent.click(screen.getByText('Исправить вердикт'));
  await waitFor(() => expect(queue.enqueue).toHaveBeenCalledTimes(2));
  expect(queue.enqueue.mock.calls[1][2]).toMatchObject({ path: '/checks/check-3/corrections',
    body: { verdict: 'correct', revision: 1, reason: 'После сверки' } });
  await waitFor(() => expect((screen.getByText('Установить') as HTMLButtonElement).disabled).toBe(false));
  fireEvent.change(screen.getByLabelText('Новый лимит'), { target: { value: '4' } });
  fireEvent.click(screen.getByText('Установить'));
  await waitFor(() => expect(queue.enqueue).toHaveBeenCalledTimes(3));
  expect(queue.enqueue.mock.calls[2][2]).toMatchObject({ path: '/appeal-quotas/g', method: 'PUT', body: { limit: 4 } });
});

it('checks claim and lease fences and treats a failed image fetch as unavailable', async () => {
  const { api } = context();
  const fake = api as unknown as StaffApi;
  const work: Work = { id: 'check-1', lane: 'checks', check: check(1) };
  expect(validLease(work)).toBe(true);
  expect(validLease({ ...work, check: { ...work.check, lease: undefined } })).toBe(false);
  api.artifact.mockRejectedValueOnce(new ApiError(503, 'UNAVAILABLE'));
  expect(await withPhoto(fake, work)).toBe(work);
  expect(await withPhoto(fake, { ...work, check: { ...work.check, imageRef: 'https://other.example/secret' } })).not.toHaveProperty('photo');
  api.command.mockResolvedValueOnce(check(1));
  expect((await renewOrRelease(fake, work, 'renew')).check.id).toBe('check-1');
  expect(await nextWork(fake, 'g', 'checks', 'check-1')).toBeUndefined();
});
