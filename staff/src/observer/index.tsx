import type { Area, Workstation } from '../app';
import { Lookup } from './lookup';
import { Printing } from './print';
import { ScanWorkplace } from './scan-workplace';

function Observer(workstation: Workstation) {
  return <><h1>Приём бланков</h1><div className="columns"><ScanWorkplace {...workstation}/><Lookup {...workstation}/></div></>;
}

export const observerAreas: Area[] = [
  { id: 'observer', title: 'Приём бланков', role: 'observer', view: Observer },
  { id: 'print', title: 'Печать', role: 'observer', view: Printing },
];
