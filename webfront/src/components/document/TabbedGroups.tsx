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
  readOnlyField,
  onEdit,
  extras,
  beforeFields,
}: {
  groups: FieldGroup[];
  values: Record<string, any>;
  posted?: boolean;
  readOnlyField?: string;
  onEdit: (key: string, value: string) => void;
  extras?: Record<string, ReactNode>;
  beforeFields?: Record<string, ReactNode>;
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
      {beforeFields?.[current.title]}
      {current.fields.length > 0 && (
        <div className="card-b">
          <FieldGrid
            fields={current.fields}
            values={values}
            posted={posted}
            readOnlyField={readOnlyField}
            required={current.required}
            onEdit={onEdit}
          />
        </div>
      )}
      {extra}
    </>
  );
}
