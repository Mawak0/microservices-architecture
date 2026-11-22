export interface Actor {
  id: string;
  roles: string[];
}

export interface Team {
  id: string;
  name: string;
}

export interface Task {
  id: string;
  number: string;
}

export interface Game {
  id: string;
  name: string;
  teams: Team[];
  tasks: Task[];
  capabilities?: { appealsAllowed?: boolean; modifiersAllowed?: boolean };
}

export interface Tournament {
  id: string;
  name: string;
  games: Game[];
}

export interface Directory {
  tournaments: Tournament[];
}

export type Service = 'intake' | 'materials' | 'review';
export interface Page<T> { items: T[]; nextOffset: number | null }

export const can = (actor: Actor, role: string) => actor.roles.includes('admin') || actor.roles.includes(role);
