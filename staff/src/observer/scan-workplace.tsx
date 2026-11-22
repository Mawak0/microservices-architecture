import { useEffect, useRef, useState } from 'react';
import { type Workstation } from '../app';
import { message } from '../messages';
import { filterUrl, useQuery } from '../query';
import type { Page } from '../types';
import { frame, readScan, type Scan } from './camera';

export interface Retake {
  id: string; checkId: string; receiptId: string; blankId: string; teamId: string; taskNumber: string;
  requiresApproval: boolean; approved: boolean; imageVersion: number;
}
interface Blank {
  blankId: string; teamId: string; taskNumber: string; modifier?: { kind: string; value: number };
}
export function ScanWorkplace(ctx: Workstation) {
  const { api, game, queue, online, setDirty } = ctx;
  const retakes = useQuery(signal => api.read<Page<Retake>>('review', filterUrl('/retakes', { gameId: game.id }), signal),
    [api, game.id, online, ctx.jobs]);
  const [retake, pickRetake] = useState<Retake>();
  const [scan, setScan] = useState<Scan>();
  const [blank, setBlank] = useState<Blank>();
  const [problem, setProblem] = useState('');
  const [busy, setBusy] = useState(false);
  const [camera, setCamera] = useState(false);
  const [devices, setDevices] = useState<MediaDeviceInfo[]>([]);
  const [device, setDevice] = useState('');
  const video = useRef<HTMLVideoElement>(null);
  const stream = useRef<MediaStream>(undefined);
  const active = useRef(true);
  const locked = useRef(false);

  useEffect(() => {
    active.current = true;
    return () => { active.current = false; stream.current?.getTracks().forEach(track => track.stop()); setDirty(false); };
  }, [setDirty]);

  async function startCamera() {
    try {
      const feed = await navigator.mediaDevices.getUserMedia({
        video: device ? { deviceId: { exact: device } } : { width: { ideal: 1920 } }, audio: false,
      });
      if (!active.current || !video.current) { feed.getTracks().forEach(track => track.stop()); return; }
      stream.current?.getTracks().forEach(track => track.stop());
      stream.current = feed;
      video.current.srcObject = feed;
      setCamera(true);
      setDevices((await navigator.mediaDevices.enumerateDevices()).filter(item => item.kind === 'videoinput'));
    } catch {
      setProblem('Камера недоступна. Проверьте разрешение или загрузите фотографию.');
    }
  }

  async function inspect(source: () => Promise<Blob>) {
    if (locked.current) return;
    locked.current = true;
    setBusy(true);
    setScan(undefined);
    setBlank(undefined);
    setProblem('');
    try {
      const latest = await readScan(await source());
      const alreadyQueued = ctx.jobs.some(job => job.state !== 'conflict' &&
        (job.body as { qrToken?: string; retakeId?: string } | undefined)?.qrToken === latest.qrToken &&
        (job.body as { retakeId?: string }).retakeId === retake?.id);
      if (alreadyQueued) { setProblem('Этот бланк уже находится в очереди отправки.'); return; }
      setScan(latest);
      setDirty(true);
      if (online && !retake) {
        try {
          setBlank(await api.command<Blank>({ service: 'intake', path: '/receipts/preview', body: { gameId: game.id, qrToken: latest.qrToken } }));
        } catch (error) {
          if (error && typeof error === 'object' && 'status' in error && Number(error.status) < 500) throw error;
        }
      }
    } catch (error) {
      setProblem(message(error));
      setScan(undefined);
      setDirty(false);
    } finally { setBusy(false); locked.current = false; }
  }

  async function send() {
    if (!scan || locked.current) return;
    locked.current = true;
    setBusy(true);
    try {
      const { qrToken, imageBase64, imageMediaType } = scan;
      const upload = { qrToken, imageBase64, imageMediaType, legibilityConfirmed: true };
      await queue.enqueue(game.id, retake ? 'Пересъёмка · задача ' + retake.taskNumber : 'Бланк ' + (blank?.taskNumber ?? ''),
        { service: 'intake', path: retake ? '/receipts/' + encodeURIComponent(retake.receiptId) + '/retakes' : '/receipts',
          body: retake ? { ...upload, retakeId: retake.id, checkId: retake.checkId } : { ...upload, gameId: game.id } });
      setScan(undefined);
      setBlank(undefined);
      pickRetake(undefined);
      setDirty(false);
      if (online) void queue.flush().catch(error => setProblem(message(error)));
    } catch { setProblem('Не удалось сохранить снимок на устройстве. Проверьте свободное место.'); }
    finally { setBusy(false); locked.current = false; }
  }

  return <section className="panel stack">
    <h2>Сканирование бланков</h2>
    <div className="row">
      {devices.length > 1 && <label>Камера<select value={device} onChange={e => setDevice(e.target.value)}>
        <option value="">По умолчанию</option>
        {devices.map(d => <option key={d.deviceId} value={d.deviceId}>{d.label || 'Камера'}</option>)}
      </select></label>}
      <button className="quiet" onClick={() => void startCamera()}>{camera ? 'Переключить камеру' : 'Включить камеру'}</button>
      <label>Загрузить фото<input type="file" accept="image/png,image/jpeg" disabled={busy} onChange={e => {
        const file = e.target.files?.[0];
        e.target.value = '';
        if (file) void inspect(async () => file);
      }}/></label>
    </div>
    <video ref={video} className="scanner-preview" hidden={!camera} autoPlay muted playsInline/>
    {camera && <button onClick={() => void inspect(() => frame(video.current!))} disabled={busy}>Снять бланк</button>}
    <label>Пересъёмка<select value={retake?.id ?? ''} disabled={busy || !!scan}
      onChange={e => pickRetake(retakes.data?.items.find(item => item.id === e.target.value))}>
      <option value="">Первичная сдача</option>
      {(retakes.data?.items ?? []).map(item => <option key={item.id} value={item.id} disabled={item.requiresApproval && !item.approved}>
        {game.teams.find(t => t.id === item.teamId)?.name ?? item.teamId} · {item.taskNumber}
        {item.requiresApproval && !item.approved ? ' · ждёт разрешения' : ''}
      </option>)}
    </select></label>
    {retakes.error != null && <p role="status">Список пересъёмок недоступен.</p>}
    {problem && <p role="alert">{problem}</p>}
    {scan && <div className="stack">
      <img className="scanner-preview" src={scan.url} alt="Снимок для отправки"/>
      <p>{blank || retake
        ? (game.teams.find(t => t.id === (blank ?? retake)?.teamId)?.name ?? (blank ?? retake)?.teamId) + ' · задача ' + (blank ?? retake)?.taskNumber
        : 'QR прочитан. Данные бланка проверит сервер.'}
        {blank?.modifier && ' · ' + (blank.modifier.kind === 'add' ? '+' : '×') + blank.modifier.value}
      </p>
      <div className="row">
        <button disabled={busy} onClick={() => void send()}>Снимок чёткий — отправить</button>
        <button className="quiet" disabled={busy} onClick={() => { setScan(undefined); setDirty(false); }}>Снять заново</button>
      </div>
    </div>}
  </section>;
}
