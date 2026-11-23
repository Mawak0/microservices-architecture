import { ApiError, json, pendingOperation, type Command, type StaffApi } from './api';

export type JobState = 'queued' | 'accepted' | 'done' | 'conflict';
export interface Job extends Command {
  key: string;
  actorId: string;
  gameId: string;
  label: string;
  state: JobState;
  createdAt: number;
  operation?: string;
  resultId?: string;
  error?: string;
}
export interface JobStore {
  list(actorId: string): Promise<Job[]>;
  put(job: Job): Promise<unknown>;
  delete(key: string): Promise<unknown>;
}
interface RemoteStatus { status: string; resourceId?: string; code?: string; lastError?: string }
const pending = (state: JobState) => state === 'queued' || state === 'accepted';
type Lock = (name: string, run: () => Promise<void>) => Promise<unknown>;

export const lockTab: Lock = (name, run) =>
  navigator.locks ? navigator.locks.request(name, { ifAvailable: true }, lock => lock ? run() : undefined) : run();

export class OperationQueue {
  private running?: Promise<unknown>;
  readonly changed = new EventTarget();

  constructor(private readonly api: Pick<StaffApi, 'actorId' | 'read' | 'submit'>, private readonly store: JobStore,
    private readonly lock: Lock = lockTab) {}

  async enqueue(gameId: string, label: string, command: Command): Promise<Job> {
    const job: Job = {
      ...command, key: command.key ?? crypto.randomUUID(), createdAt: Date.now(),
      gameId, label, actorId: this.api.actorId, state: 'queued',
    };
    await this.store.put(job);
    this.notify();
    return job;
  }

  list() { return this.store.list(this.api.actorId); }

  async remove(job: Job) {
    if (job.actorId !== this.api.actorId || pending(job.state)) return;
    await this.store.delete(job.key);
    this.notify();
  }

  flush(): Promise<unknown> {
    if (!this.running) {
      this.running = this.lock('olympiad-outbox:' + this.api.actorId, async () => {
        const jobs = (await this.list()).filter(job => pending(job.state)).sort((a, b) => a.createdAt - b.createdAt);
        for (const job of jobs) {
          const updated = await advance(job, this.api);
          if (updated !== job) {
            await this.store.put(updated);
            this.notify();
          }
        }
      }).finally(() => { this.running = undefined; });
    }
    return this.running;
  }

  private notify() { this.changed.dispatchEvent(new Event('change')); }
}

export async function advance(job: Job, api: Pick<StaffApi, 'read' | 'submit'>): Promise<Job> {
  try {
    if (job.operation) {
      const status = await api.read<RemoteStatus>(job.service, job.operation, AbortSignal.timeout(8_000), job.gameId);
      if (status.status === 'Completed') return complete(job, status.resourceId);
      if (status.status === 'Failed') return { ...job, state: 'conflict', error: status.lastError ?? status.code ?? 'REQUEST_FAILED' };
      return job;
    }
    const response = await api.submit(job, AbortSignal.timeout(12_000));
    const location = pendingOperation(response);
    if (location) {
      if (!/^\/operations\/[a-zA-Z0-9_-]+$/.test(location)) throw new ApiError(422, 'INVALID_OPERATION');
      return { ...job, operation: location, state: 'accepted' };
    }
    const data = await json<{ id?: string } | undefined>(response);
    return complete(job, data?.id);
  } catch (error) {
    if (!(error instanceof ApiError)) return job;
    if ([401, 408, 429].includes(error.status) || error.status >= 500) return job;
    if (job.operation && error.status === 404) return { ...job, operation: undefined, state: 'queued' };
    if (error.code === 'SESSION_CHANGED') return job;
    return { ...job, state: 'conflict', error: error.code };
  }
}

function complete(job: Job, resultId?: string): Job {
  return { ...job, body: undefined, token: undefined, state: 'done', resultId };
}
