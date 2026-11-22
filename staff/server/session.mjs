import { randomBytes } from 'node:crypto';

export class Sessions {
  #entries = new Map();

  constructor(clock = Date.now, maximum = 5000) {
    this.clock = clock;
    this.maximum = maximum;
  }

  create(value, lifetime) {
    this.prune();
    if (this.#entries.size >= this.maximum) throw new Error('Session capacity exceeded');
    const id = randomBytes(32).toString('base64url');
    this.#entries.set(id, { value, expires: this.clock() + lifetime * 1000 });
    return id;
  }

  get(id) {
    const entry = this.#entries.get(id);
    if (!entry) return undefined;
    if (entry.expires > this.clock()) return entry.value;
    this.delete(id);
  }

  delete(id) { this.#entries.delete(id); }

  prune() {
    for (const [id, entry] of this.#entries)
      if (entry.expires <= this.clock()) this.delete(id);
  }
}
