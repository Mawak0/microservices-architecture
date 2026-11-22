import type { Area } from '../app';
import { Administration } from './admin';
import { Checker } from './checker';

export const reviewAreas: Area[] = [
  { id: 'checker', title: 'Проверка ответов', role: 'checker', view: Checker },
  { id: 'admin', title: 'Администрирование', role: 'admin', view: Administration },
];
