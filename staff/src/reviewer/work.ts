import { type StaffApi } from '../api';

export type Verdict = 'correct' | 'incorrect';
export type Lane = 'checks' | 'appeals';
export interface Lease { token: string; expiresAt: string }
export interface Check {
  id: string; receiptId: string; teamId: string; taskNumber: string; referenceAnswer?: string; imageRef?: string;
  imageVersion: number; revision: number; status: string; verdict?: Verdict; originalVerdict?: Verdict;
  lease?: Lease; history?: { verdict: Verdict; actorId: string; recordedAt: string; reason?: string }[];
}
export interface Appeal { id: string; check: Check; reason?: string }
export interface Work { check: Check; id: string; lane: Lane; reason?: string; photo?: Blob }
export interface Activity { assignments: Check[]; decisions: Check[]; nextOffset: number | null }
export const verdictText = (value?: Verdict) => value === 'correct' ? 'Верно' : value === 'incorrect' ? 'Неправильно' : 'Ожидает';
export const opposite = (value?: Verdict): Verdict => value === 'correct' ? 'incorrect' : 'correct';
export const validLease = (work: Work) => !!work.check.lease && Date.parse(work.check.lease.expiresAt) > Date.now();

export async function withPhoto(api: StaffApi, work: Work, signal?: AbortSignal): Promise<Work> {
  const path = work.check.imageRef;
  if (path && /^\/receipts\/[a-zA-Z0-9_-]+\/image(?:\/versions\/\d+)?$/.test(path)) {
    try { return { ...work, photo: await api.artifact('intake', path, signal) }; }
    catch { return work; }
  }
  return work;
}

export async function nextWork(api: StaffApi, gameId: string, lane: Lane, exclude?: string): Promise<Work | undefined> {
  if (lane === 'checks') {
    const check = await api.command<Check | undefined>({ service: 'review', path: '/checks/next', body: { gameId } });
    if (check?.id && check.lease && check.id !== exclude) return withPhoto(api, { id: check.id, lane, check });
  } else {
    const appeal = await api.command<Appeal | undefined>({ service: 'review', path: '/appeals/next', body: { gameId } });
    if (appeal?.id && appeal.check.lease) return withPhoto(api, { ...appeal, lane });
  }
}

export async function renewOrRelease(api: StaffApi, work: Work, action: 'renew' | 'release'): Promise<Work> {
  const value = await api.command<Check | Appeal | undefined>({
    service: 'review', path: '/' + work.lane + '/' + encodeURIComponent(work.id) + '/' + action,
    token: work.check.lease?.token,
  });
  if (!value) return work;
  const check = 'check' in value ? value.check : value;
  return check.lease ? { ...work, check } : work;
}
