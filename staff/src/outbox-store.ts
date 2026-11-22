import type { Job, JobStore } from './queue';

export class OutboxStore implements JobStore {
  private readonly opened: Promise<IDBDatabase>;

  constructor(factory: IDBFactory = indexedDB, name = 'olympiad-staff') {
    this.opened = new Promise((resolve, reject) => {
      const open = factory.open(name, 1);
      open.onupgradeneeded = () => {
        const store = open.result.createObjectStore('operations', { keyPath: 'key' });
        store.createIndex('actor', 'actorId');
      };
      open.onsuccess = () => resolve(open.result);
      open.onerror = () => reject(open.error);
    });
  }

  list(actorId: string): Promise<Job[]> {
    return this.run('readonly', store => store.index('actor').getAll(actorId));
  }

  put(job: Job): Promise<IDBValidKey> {
    return this.run('readwrite', store => store.put(job));
  }

  delete(key: string): Promise<undefined> {
    return this.run('readwrite', store => store.delete(key));
  }

  private async run<T>(mode: IDBTransactionMode, action: (store: IDBObjectStore) => IDBRequest<T>): Promise<T> {
    const database = await this.opened;
    return new Promise((resolve, reject) => {
      const transaction = database.transaction('operations', mode);
      const request = action(transaction.objectStore('operations'));
      transaction.oncomplete = () => resolve(request.result);
      transaction.onabort = () => reject(transaction.error);
      transaction.onerror = () => reject(transaction.error);
    });
  }
}
