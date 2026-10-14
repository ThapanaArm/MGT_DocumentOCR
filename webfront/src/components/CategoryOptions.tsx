import type { ApDocCategory } from '../api/masters';

/** The full name of a document type, for places that show it outside the dropdown (a list column,
 *  a heading). A grouped choice reads as its heading plus its own name — "Liability Recording -
 *  Inventory — Purchase Order" — since "Purchase Order" alone says nothing on a row of its own. */
export function categoryLabel(c: Pick<ApDocCategory, 'label' | 'group'>): string {
  const group = String(c.group ?? '').trim();
  return group ? `${group} — ${c.label}` : c.label;
}

/** <option> list for the Document Type dropdown, with choices that share a group nested under one
 *  heading. Inventory is one entry in the list with Delivery and Purchase Order under it rather
 *  than two near-identical lines, which is what made the list hard to read at a glance. */
export default function CategoryOptions({ items }: { items: ApDocCategory[] }) {
  const out: React.ReactNode[] = [];
  let i = 0;
  while (i < items.length) {
    const group = String(items[i].group ?? '').trim();
    if (!group) {
      const c = items[i];
      out.push(<option key={c.id} value={c.id}>{c.label}</option>);
      i += 1;
      continue;
    }
    // Consecutive entries sharing a heading travel together; the server lists them that way.
    const run: ApDocCategory[] = [];
    while (i < items.length && String(items[i].group ?? '').trim() === group) {
      run.push(items[i]);
      i += 1;
    }
    out.push(
      <optgroup key={group} label={group}>
        {run.map((c) => (
          <option key={c.id} value={c.id}>{c.label}</option>
        ))}
      </optgroup>,
    );
  }
  return <>{out}</>;
}
