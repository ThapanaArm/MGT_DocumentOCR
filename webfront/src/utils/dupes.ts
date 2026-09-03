import { M_LABEL } from '../constants/fields';
import type { MasterRow, MastersData } from '../api/masters';

/* Client-side duplicate detection before adding master rows — ports
   taxDigits/normName/simJS/findDupes from app.js. */

const taxDigits = (s: unknown) => String(s || '').replace(/\D/g, '');

const normName = (s: unknown) =>
  String(s || '')
    .toLowerCase()
    .replace(
      /บริษัท|จำกัด|มหาชน|หจก\.|ห้างหุ้นส่วนจำกัด|co\.,?\s*ltd\.?|company|limited|public|pcl\.?|corp\.?|inc\.?/g,
      '',
    )
    .replace(/[^a-z0-9ก-๙]/g, '');

function simJS(a: string, b: string): number {
  a = normName(a);
  b = normName(b);
  if (!a || !b) return 0;
  if (a === b) return 1;
  if (a.includes(b) || b.includes(a)) return 0.92;
  const bg = (s: string) => {
    const o: string[] = [];
    for (let i = 0; i < s.length - 1; i++) o.push(s.slice(i, i + 2));
    return o;
  };
  const A = bg(a);
  const B = bg(b);
  if (!A.length || !B.length) return 0;
  const pool = B.slice();
  let hit = 0;
  A.forEach((x) => {
    const i = pool.indexOf(x);
    if (i > -1) {
      hit++;
      pool.splice(i, 1);
    }
  });
  return (2 * hit) / (A.length + B.length);
}

export interface Dupe {
  row: MasterRow;
  score: number;
  reason: string;
}

export function findDupes(
  masters: MastersData,
  kind: string,
  nameVal: string,
  taxVal: string | null,
  filterFn?: (r: MasterRow) => boolean,
): Dupe[] {
  const rows = masters[kind] || [];
  const nameField = M_LABEL[kind];
  const taxField = kind === 'customers' || kind === 'vendors' ? 'TaxId' : null;
  return rows
    .filter((r) => !filterFn || filterFn(r))
    .map((r) => {
      let score = 0;
      let reason = '';
      if (taxField && taxVal && taxDigits(taxVal) && taxDigits(r[taxField]) === taxDigits(taxVal)) {
        score = 1;
        reason = 'Matching Tax ID';
      } else {
        const s = simJS(nameVal, r[nameField]);
        if (s > score) {
          score = s;
          reason = 'Similar name ' + Math.round(s * 100) + '%';
        }
      }
      return { row: r, score, reason };
    })
    .filter((x) => x.score >= 0.55)
    .sort((a, b) => b.score - a.score)
    .slice(0, 4);
}
