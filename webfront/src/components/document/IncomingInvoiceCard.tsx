import type { ComponentProps } from 'react';
import TabbedGroups from './TabbedGroups';
import TaxDataTable from './TaxDataTable';
import WhtTable from './WhtTable';
import FieldGrid from './FieldGrid';
import { II_GROUPS, II_ONETIME_H } from '../../constants/fields';
import { fmt, num } from '../../utils/format';

/* SAP-faithful "Enter Supplier Invoice" (FB60) page for the Incoming Invoice
   module — Transaction/Balance strip, FB60 tabs beside a vendor panel. */

const TRANSACTIONS: [string, string][] = [
  ['Invoice', 'R Invoice'],
  ['CreditMemo', 'Credit Memo'],
  ['SubsequentDebit', 'Subsequent Debit'],
  ['SubsequentCredit', 'Subsequent Credit'],
];

interface Props {
  values: Record<string, any>;
  posted: boolean;
  onEdit: (key: string, value: string) => void;
  glItems: any[];
  taxProps: ComponentProps<typeof TaxDataTable>;
  whtProps: ComponentProps<typeof WhtTable>;
}

export default function IncomingInvoiceCard({ values: h, posted, onEdit, glItems, taxProps, whtProps }: Props) {
  const glSum = (glItems || []).reduce((a, g) => a + num(g.amount), 0);
  const balance = num(h.totalAmount) - glSum;
  const balanced = Math.abs(balance) < 0.005;

  return (
    <>
    <div className="card">
      <div className="card-h">
        <h2>Incoming Invoice — Enter Supplier Invoice (Without PO)</h2>
      </div>

      {/* Transaction + Balance strip (SAP top row) */}
      <div
        className="card-b"
        style={{ display: 'flex', alignItems: 'center', gap: 20, flexWrap: 'wrap', paddingBottom: 14, borderBottom: '1px solid var(--line-soft)' }}
      >
        <div className="f" style={{ maxWidth: 260, flex: '0 0 auto' }}>
          <label>Transaction</label>
          <select value={h.transaction ?? ''} disabled={posted} onChange={(e) => onEdit('transaction', e.target.value)}>
            {TRANSACTIONS.map(([v, t]) => (
              <option key={v} value={v}>{t}</option>
            ))}
          </select>
        </div>
        <div style={{ marginLeft: 'auto', display: 'flex', alignItems: 'center', gap: 10 }}>
          <span
            style={{
              width: 12, height: 12, borderRadius: 3,
              background: balanced ? 'var(--ok, #16a34a)' : 'var(--warn, #d97706)',
            }}
            title={balanced ? 'Balance OK' : 'Not balanced'}
          />
          <label style={{ margin: 0 }}>Balance</label>
          <span
            style={{
              fontVariantNumeric: 'tabular-nums', fontWeight: 600,
              padding: '7px 14px', border: '1px solid var(--line)', borderRadius: 'var(--r2, 8px)',
              background: 'var(--line-soft)',
              color: balanced ? 'inherit' : 'var(--warn, #d97706)',
            }}
          >
            {fmt(balance)} {h.currency || 'THB'}
          </span>
        </div>
      </div>

      {/* FB60 tabs + vendor panel */}
      <div
        className="ii-body"
      >
        <div style={{ minWidth: 0 }}>
          <TabbedGroups
            groups={II_GROUPS}
            values={h}
            posted={posted}
            onEdit={onEdit}
            extras={{
              Tax: <TaxDataTable {...taxProps} bare />,
              'Withholding Tax': <WhtTable {...whtProps} bare />,
            }}
          />
        </div>

        {/* <aside
          style={{
            border: '1px solid var(--line)', borderRadius: 'var(--r2, 10px)',
            background: 'var(--line-soft)', padding: 16, margin: '16px 20px 20px 0',
          }}
        >
          <div className="hint" style={{ textTransform: 'uppercase', letterSpacing: '.04em', fontSize: 11 }}>
            Vendor{h.vendorCode || h.partnerCode ? ' · ' + (h.vendorCode || h.partnerCode) : ''}
          </div>
          <div style={{ fontWeight: 700, fontSize: 15, margin: '4px 0 2px' }}>{h.vendorName || '—'}</div>
          {h.vendorTaxId && <div className="hint">Tax ID {h.vendorTaxId}</div>}
          {(h.addressCity || h.addressStreet) && (
            <div className="hint" style={{ marginTop: 6 }}>{[h.addressStreet, h.addressCity].filter(Boolean).join(' · ')}</div>
          )}
          {h.vendorEmail && <div className="hint" style={{ marginTop: 6 }}>✉ {h.vendorEmail}</div>}
          {h.bankAccountNo && (
            <>
              <hr style={{ border: 0, borderTop: '1px solid var(--line)', margin: '12px 0' }} />
              <div className="hint" style={{ textTransform: 'uppercase', letterSpacing: '.04em', fontSize: 11 }}>Bank Account</div>
              <div style={{ fontFamily: 'monospace', marginTop: 4 }}>{h.bankAccountNo}</div>
            </>
          )}
        </aside> */}
      </div>
    </div>

    {/* One-time vendor Address & Bank Data (SAP FB60 popup → card) */}
    {h.oneTimeVendor && (
      <div className="card">
        <div className="card-h">
          <h2>Address and Bank Data — One-Time Vendor</h2>
        </div>
        <div className="card-b">
          <FieldGrid fields={II_ONETIME_H} values={h} posted={posted} onEdit={onEdit} />
        </div>
      </div>
    )}
    </>
  );
}
