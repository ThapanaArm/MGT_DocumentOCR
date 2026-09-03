import { useState, type ReactNode } from 'react';
import FieldGrid from './FieldGrid';
import type { FieldGroup } from '../../constants/fields';

/* Ports tabbedGroupsHtml() — MIRO / Incoming-Invoice grouped header fields
   shown as tabs. `extras` lets a tab render extra content (e.g. the item
   table under the PO Reference tab, mirroring the SAP MIRO screen). */
export default function TabbedGroups({
  groups,
  values,
  posted,
  onEdit,
  extras,
}: {
  groups: FieldGroup[];
  values: Record<string, any>;
  posted?: boolean;
  onEdit: (key: string, value: string) => void;
  extras?: Record<string, ReactNode>;
}) {
  const [active, setActive] = useState(groups[0]?.title ?? '');
  const current = groups.find((g) => g.title === active) || groups[0];
  const extra = extras?.[current.title];
  return (
    <>
      <div className="tabs">
        {groups.map((g) => (
          <button
            key={g.title}
            className={g.title === current.title ? 'on' : ''}
            onClick={() => setActive(g.title)}
          >
            {g.title}
          </button>
        ))}
      </div>
      {current.fields.length > 0 && (
        <div className="card-b">
          <FieldGrid
            fields={current.fields}
            values={values}
            posted={posted}
            required={current.required}
            onEdit={onEdit}
          />
        </div>
      )}
      {extra}
    </>
  );
}
