import type { Actor, Directory, Service } from './types';

export type HttpMethod = 'POST' | 'PUT';
export interface Command {
  service: Service;
  path: string;
  method?: HttpMethod;
  body?: object;
  token?: string;
  key?: string;
}

export class ApiError extends Error {
  constructor(readonly status: number, readonly code: string) {
    super(code);
  }
}

export async function json<T>(response: Response): Promise<T> {
  if (response.status === 204) return undefined as T;
  const value = await response.json();
  if (!response.ok) throw new ApiError(response.status, value.code ?? 'REQUEST_FAILED');
  return value as T;
}

export type Requester = typeof fetch;
export class StaffApi {
  constructor(readonly actorId: string, private readonly send: Requester = (input, options) => fetch(input, options)) {}

  read<T>(service: Service, path: string, signal?: AbortSignal) {
    return this.request(service, path, { signal }).then(json<T>);
  }

  async command<T>(spec: Command): Promise<T> {
    return json<T>(await this.submit({ ...spec, key: spec.key ?? crypto.randomUUID() }));
  }

  submit(command: Command, signal?: AbortSignal) {
    const headers: Record<string, string> = { 'Idempotency-Key': command.key ?? '' };
    if (command.token) headers['Assignment-Token'] = command.token;
    if (command.body !== undefined) headers['Content-Type'] = 'application/json';
    return this.request(command.service, command.path, {
      method: command.method ?? 'POST',
      body: command.body === undefined ? undefined : JSON.stringify(command.body),
      headers,
      signal,
    });
  }

  async artifact(service: Service, path: string, signal?: AbortSignal): Promise<Blob> {
    const response = await this.request(service, path, { signal });
    if (!response.ok) await json(response);
    return response.blob();
  }

  private request(service: Service, path: string, init: RequestInit) {
    const headers = new Headers(init.headers);
    headers.set('X-Staff-Actor', this.actorId);
    return this.send('/api/' + service + path, { ...init, headers });
  }
}

export const getSession = () => fetch('/api/session').then(json<Actor>);
export const getDirectory = () => fetch('/api/context').then(json<Directory>);
