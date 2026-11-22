import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { ApiError } from '../src/api';
import type { Workstation } from '../src/app';
import { Lookup } from '../src/observer/lookup';
import { Printing } from '../src/observer/print';
import { ScanWorkplace } from '../src/observer/scan-workplace';
import { frame, readScan } from '../src/observer/camera';

vi.mock('../src/observer/camera', () => ({ frame: vi.fn(), readScan: vi.fn() }));
const scan = { qrToken: 'original-qr', imageBase64: 'AA==', imageMediaType: 'image/png' as const, url: 'data:image/png;base64,AA==' };
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.resetAllMocks(); });
const page = (items: object[]) => ({ items, nextOffset: null });

function context(role = 'observer', online = true) {
  const api = {
    read: vi.fn(async (_, path: string) => {
      if (path.startsWith('/retakes')) return page([{ id: 'retake', checkId: 'check', receiptId: 'receipt',
        teamId: 'team', taskNumber: '3', requiresApproval: true, approved: true }]);
      if (path.startsWith('/checks/search')) return page([{ id: 'check', taskNumber: '3' }]);
      if (path.endsWith('/appeal-availability')) return { allowed: true, remaining: 2 };
      if (path.startsWith('/receipts')) return page([{ id: 'receipt', teamId: 'team', taskNumber: '3', acceptedAt: '2026-11-22T12:00:00Z' }]);
      if (path.startsWith('/blanks')) return page([{ id: 'blank', number: 1, taskNumber: '3' }]);
      if (path.startsWith('/print-jobs')) return page([{ id: 'print-1', status: 'completed' }]);
      if (path.startsWith('/kits?')) return page([{ id: 'kit' }]);
      return { id: 'kit', blankIds: ['blank'] };
    }),
    command: vi.fn(async () => ({ blankId: 'blank', teamId: 'team', taskNumber: '3', modifier: { kind: 'add', value: 3 } })),
    artifact: vi.fn(async () => new Blob(['%PDF'])),
  };
  const queue = { enqueue: vi.fn(async () => ({})), flush: vi.fn(async () => {}) };
  const value = { actor: { id: 'alice', roles: [role] },
    game: { id: 'game', name: 'Бонусы', tasks: [{ id: 'task', number: '3' }],
      teams: [{ id: 'team', name: 'Вектор' }], capabilities: { modifiersAllowed: true } },
    api, queue, jobs: [], online, setDirty: vi.fn() };
  return { props: value as unknown as Workstation, api, queue };
}
const file = () => new File(['fake'], 'form.png', { type: 'image/png' });
const choosePhoto = () => fireEvent.change(screen.getByLabelText('Загрузить фото'), { target: { files: [file()] } });

it('uses the final uploaded photo and sends it once after a single confirmation', async () => {
  const { props, api, queue } = context();
  vi.mocked(readScan).mockResolvedValue(scan);
  render(<ScanWorkplace {...props}/>);
  choosePhoto();
  await screen.findByText(/Вектор · задача 3/);
  expect(api.command).toHaveBeenCalledWith(expect.objectContaining({ path: '/receipts/preview' }));
  expect(queue.enqueue).not.toHaveBeenCalled();
  const send = screen.getByRole('button', { name: /Снимок чёткий/ });
  fireEvent.click(send);
  fireEvent.click(send);
  await waitFor(() => expect(queue.enqueue).toHaveBeenCalledTimes(1));
  expect(queue.enqueue.mock.calls[0][2]).toMatchObject({ path: '/receipts',
    body: { gameId: 'game', qrToken: scan.qrToken, imageBase64: scan.imageBase64, legibilityConfirmed: true } });
  await waitFor(() => expect(queue.flush).toHaveBeenCalled());
  expect(screen.queryByAltText('Снимок для отправки')).toBeNull();
});

it('stages a requested retake offline and rejects an unreadable photo', async () => {
  const { props, queue, api } = context('observer', false);
  vi.mocked(readScan).mockRejectedValueOnce(new ApiError(422, 'QR_UNREADABLE')).mockResolvedValue(scan);
  render(<ScanWorkplace {...props}/>);
  await screen.findByRole('option', { name: /Вектор/ });
  fireEvent.change(screen.getByLabelText('Пересъёмка'), { target: { value: 'retake' } });
  choosePhoto();
  expect(await screen.findByRole('alert')).toBeDefined();
  choosePhoto();
  fireEvent.click(await screen.findByRole('button', { name: /Снимок чёткий/ }));
  await waitFor(() => expect(queue.enqueue).toHaveBeenCalled());
  expect(queue.enqueue.mock.calls[0][2]).toMatchObject({ path: '/receipts/receipt/retakes',
    body: { checkId: 'check', retakeId: 'retake' } });
  expect(api.command).not.toHaveBeenCalled();
  expect(queue.flush).not.toHaveBeenCalled();
});

it('captures from a live camera and stops tracks when leaving', async () => {
  const { props } = context();
  const stop = vi.fn();
  Object.defineProperty(navigator, 'mediaDevices', { configurable: true, value: {
    enumerateDevices: async () => [{ kind: 'videoinput', deviceId: '1', label: 'Сканер' }, { kind: 'videoinput', deviceId: '2' }],
    getUserMedia: async () => ({ getTracks: () => [{ stop }] }),
  } });
  vi.mocked(frame).mockResolvedValue(file());
  vi.mocked(readScan).mockResolvedValue(scan);
  const { unmount } = render(<ScanWorkplace {...props}/>);
  fireEvent.click(screen.getByText('Включить камеру'));
  fireEvent.click(await screen.findByText('Снять бланк'));
  await screen.findByRole('button', { name: 'Снимок чёткий — отправить' });
  fireEvent.click(screen.getByText('Снять заново'));
  unmount();
  expect(stop).toHaveBeenCalledTimes(1);
});

it('finds the answer and requests an appeal only after confirmation', async () => {
  const { props, queue } = context();
  vi.spyOn(window, 'confirm').mockReturnValueOnce(false).mockReturnValueOnce(true);
  render(<Lookup {...props}/>);
  fireEvent.click(await screen.findByRole('button', { name: 'Апелляция' }));
  await screen.findByText(/Доступно обращений в игре — 2/);
  fireEvent.change(screen.getByLabelText('Причина'), { target: { value: 'Проверить ответ' } });
  const submit = screen.getByRole('button', { name: 'Подать апелляцию' });
  fireEvent.click(submit);
  expect(queue.enqueue).not.toHaveBeenCalled();
  fireEvent.click(submit);
  expect(queue.enqueue.mock.calls[0][2]).toMatchObject({ path: '/appeals', body: { checkId: 'check', reason: 'Проверить ответ' } });
});

it('prepares existing material and permits additional issuance only to an administrator', async () => {
  const { props, queue } = context('admin');
  vi.spyOn(window, 'confirm').mockReturnValue(true);
  render(<Printing {...props}/>);
  fireEvent.change(screen.getByLabelText('Команда'), { target: { value: 'team' } });
  fireEvent.change(screen.getByLabelText('Задача'), { target: { value: 'task' } });
  fireEvent.click(await screen.findByLabelText(/№ 1/));
  fireEvent.click(screen.getByRole('button', { name: 'Подготовить PDF' }));
  await waitFor(() => expect(queue.enqueue).toHaveBeenCalledTimes(1));
  expect(queue.enqueue.mock.calls[0][2]).toMatchObject({ path: '/print-jobs', body: { blankIds: ['blank'] } });
  await waitFor(() => expect((screen.getByRole('button', { name: 'Выпустить дополнительные' }) as HTMLButtonElement).disabled).toBe(false));
  fireEvent.click(screen.getByRole('button', { name: 'Выпустить дополнительные' }));
  await waitFor(() => expect(queue.enqueue).toHaveBeenCalledTimes(2));
  expect(queue.enqueue.mock.calls[1][2]).toMatchObject({ path: '/blanks', body: { count: 1 } });
  expect(screen.getByRole('button', { name: 'Скачать PDF' })).toBeDefined();
});

it('downloads a checked PDF and hides additional issuance from observers', async () => {
  const { props, api, queue } = context();
  URL.createObjectURL = vi.fn(() => 'blob:pdf');
  URL.revokeObjectURL = vi.fn();
  const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});
  render(<Printing {...props}/>);
  expect(screen.queryByRole('button', { name: 'Выпустить дополнительные' })).toBeNull();
  fireEvent.click(await screen.findByRole('button', { name: 'Скачать PDF' }));
  await waitFor(() => expect(click).toHaveBeenCalled());
  expect(api.artifact).toHaveBeenCalledWith('materials', '/print-jobs/print-1/artifact');
  fireEvent.change(screen.getByLabelText('Команда'), { target: { value: 'team' } });
  fireEvent.click(screen.getByText('Весь комплект'));
  await waitFor(() => expect(queue.enqueue).toHaveBeenCalled());
});
