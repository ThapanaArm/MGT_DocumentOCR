import { Fragment, useEffect, useState, type ReactNode } from 'react';
import { getMgtSales, getZohoAccountFullByCode } from '../../api/zoho';
import type { DocLine } from '../../api/documents';
import type { OcrProvider } from '../../api/masters';
import type {
  ZohoDeal,
  ZohoDealItem,
  ZohoSalesOrderFlow,
  ZohoSalesOrderPreview,
  ZohoSalesOrderResult,
  SalesUser,
} from '../../api/zoho';
import CompareModal, { type CompareCandidateItem } from './CompareModal';
import type { CompareField } from '../../api/compare';
import { SEND_DISABLED } from '../../constants/flags';
import DateInput, { formatDate } from '../common/DateInput';
import type { DeliveryDateGroup } from './SapSalesOrderEditor';
import type { SoGroupState } from '../../api/documents';
import SoGroupPanel, { groupStatus, type GroupShipToControl } from './SoGroupPanel';
import MaterialSearchSelect from './MaterialSearchSelect';

/* Sales person (Owner) of the NEW Deal (12 Nov 2026), picked from the MGT Sales list (Ms_User,
   Sales Organization 1000). Defaults to the customer's Account Owner; when that user is not active in
   Zoho any more (Zoho rejects the Owner id), CS must pick someone before Send. On Send the backend
   finds the Zoho user by Email, then English name. An existing Deal keeps its own Owner (read-only). */
function DealOwnerPicker({ preview, salesUserId, onChange, posted }: {
  preview: ZohoSalesOrderPreview;
  salesUserId: string;
  onChange: (salesUserId: string) => void;
  posted: boolean;
}) {
  const [sales, setSales] = useState<SalesUser[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  useEffect(() => {
    setLoading(true);
    setError('');
    getMgtSales()
      .then((r) => setSales(r.sales))
      .catch((e) => setError(e instanceof Error ? e.message : 'Could not read the Sales list'))
      .finally(() => setLoading(false));
  }, []);

  const accountOwnerInactive = preview.dealOwnerActive === false;

  // Compact (12 Nov 2026): the Responsible Sales box with just a labelled dropdown, plus one short
  // red line when the Account Owner can't own the new Deal.
  return (
    <div className="notice check-box" style={{ margin: '12px 0', padding: 12 }}>
      <div className="row" style={{ gap: 12, alignItems: 'center', flexWrap: 'wrap' }}>
        <b className="check-label">Responsible Sales / Deal Owner:</b>
        <div style={{ flex: '1 1 420px', maxWidth: 560 }}>
          <MaterialSearchSelect
            options={sales.map((u) => ({
              value: u.userId,
              description: u.name,
              label: u.email ? `${u.name} (${u.email})` : u.name,
              searchText: [u.name, u.email].filter(Boolean).join(' '),
            }))}
            value={salesUserId}
            disabled={posted || loading}
            minWidth={420}
            emptyLabel={loading
              ? 'Loading Sales…'
              : accountOwnerInactive ? '— Select Sales —' : `Account Owner${preview.dealOwnerName ? `: ${preview.dealOwnerName}` : ''}`}
            searchPlaceholder="Type a Sales name or email…"
            ariaLabel="Search and select the Sales person"
            onChange={(value) => onChange(value)}
          />
        </div>
      </div>
      {accountOwnerInactive && !salesUserId && (
        <div className="hint" style={{ color: 'var(--red, #c0392b)', marginTop: 4 }}>
          Account Owner “{preview.dealOwnerName}” is inactive in Zoho — select a Sales.
        </div>
      )}
      {error && <div className="hint" style={{ color: 'var(--red, #c0392b)', marginTop: 4 }}>{error}</div>}
    </div>
  );
}

function DealOwnerPreview({ preview }: { preview: ZohoSalesOrderPreview }) {
  const [accountOwner, setAccountOwner] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  useEffect(() => {
    setAccountOwner('');
    setError('');
    setLoading(false);
    if (preview.dealOwnerName || !preview.flow?.newDeal || !preview.accountCode) return;
    let cancelled = false;
    setLoading(true);
    getZohoAccountFullByCode(preview.accountCode)
      .then((account) => {
        if (cancelled) return;
        const owner = account.fields.find((f) => f.label.toLowerCase() === 'owner')?.value;
        if (owner) setAccountOwner(owner);
        else setError('No responsible salesperson found on the Customer Account.');
      })
      .catch(() => { if (!cancelled) setError('Could not read the responsible salesperson. Please reload the preview.'); })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [preview.dealOwnerName, preview.flow?.newDeal, preview.accountCode]);
  const name = preview.dealOwnerName || accountOwner;
  return (
    <div className="notice check-box" style={{ margin: '12px 0', padding: 12 }}>
      <b className="check-label">Responsible Sales / Deal Owner: </b>
      {name ? <strong>{name}</strong> : <span>{loading ? 'Reading Customer Account…' : error || preview.dealOwnerError || 'Could not determine the Deal owner. Please reload the preview.'}</span>}
      {preview.flow?.newDeal && name && <div className="hint">Sales Employee from Customer Account — assigned as owner of the new Deal</div>}
    </div>
  );
}

/* The Zoho Sales Order, lifted whole onto the document page as one editable table -- what
   replaced the old per-line "Material - Row N" comparison cards + the "Confirm Submission to
   Zoho CRM" popup. It shows exactly the fields that get sent to Zoho (header + Ordered Items),
   every one editable inline, plus any lines that couldn't be matched to a Deal Item (each with
   an "Ask AI to suggest a match" button). The person edits here directly and presses Send at the
   bottom -- no separate confirm popup. The heavy lifting (fetching the preview, holding the edit
   state, and actually creating the record) all stays in DocumentPage; this is the presentation. */

type LineEdit = { quantity: string; unitPrice: string; unit: string; description: string; materialId?: string };

export default function ZohoSalesOrderEditor({
  preview,
  loading,
  error,
  selectedDealId,
  docLines,
  dealOfItem,
  dealLabel,
  dealByKey,
  providers,
  header,
  onHeaderChange,
  lineEdits,
  onLineChange,
  onUseAiMatch,
  sending,
  result,
  onSend,
  onViewPayload,
  posted,
  dateGroups = [],
  posts = [],
  shipToMissing = false,
  newDealForecast = {},
  onNewDealForecast,
  forecastMissing = false,
  dealRemarks = {},
  onDealRemarkChange,
  groupShipTo,
  salesUserId = '',
  onOwnerChange,
  ownerMissing = false,
}: {
  /** The merged preview (backend defaults + any AI-confirmed matches) -- fetched by DocumentPage
   *  as soon as a Deal is picked, so this table is visible without pressing Send first. */
  preview: ZohoSalesOrderPreview | null;
  loading: boolean;
  error: string | null;
  selectedDealId: string;
  docLines: DocLine[];
  /** The Deal of a line (by itemNo; null = a new Deal) -- its Deal Items are the candidate pool for the
   *  AI-suggest tool on a line the automatic match skipped. One PO may hold several Deals (16 Nov 2026). */
  dealOfItem: (itemNo: string) => ZohoDeal | null;
  /** Display name of a Deal key ("New Deal 2" for a new one). */
  dealLabel: (key: string) => string;
  /** The open Deal behind a key (null = a new Deal) -- its Deal Number / delivery date tell twin Deals apart. */
  dealByKey?: (key: string) => ZohoDeal | null;
  providers: OcrProvider[];
  header: { subject: string; customerRef: string; deliveryDate: string; paymentTerms: string; paymentCurrency: string; incoterms: string; taxId: string; remarks: string };
  onHeaderChange: (patch: Partial<ZohoSalesOrderEditorHeader>) => void;
  lineEdits: Record<string, LineEdit>;
  onLineChange: (itemNo: unknown, patch: Partial<LineEdit>) => void;
  onUseAiMatch: (itemNo: unknown, item: ZohoDealItem) => void;
  sending: boolean;
  result: ZohoSalesOrderResult | null;
  onSend: () => void;
  /** Opens the "View Payload" modal showing the exact JSON that will be POSTed to Zoho. */
  onViewPayload: () => void;
  posted: boolean;
  /** Lines grouped by SO Group + delivery date (deliveryDateGroups). Same rule as SAP: one Zoho Sales
   *  Order per group, all created from this document by one Send. */
  dateGroups?: DeliveryDateGroup[];
  /** Send results per group (ocr.SalesOrderPost): created groups are skipped on the next Send. */
  posts?: SoGroupState[];
  /** No Ship-to selected/mapped yet -- Send stays disabled until one is set. */
  shipToMissing?: boolean;
  /** New Deal key -> Forecast Status CS chose ("Yes" / "No"); a new Deal has no default (16 Nov 2026). */
  newDealForecast?: Record<string, 'Yes' | 'No'>;
  onNewDealForecast?: (key: string, value: 'Yes' | 'No') => void;
  /** A new Deal still has no Forecast Status chosen -- Send stays disabled. */
  forecastMissing?: boolean;
  /** Several Deals: Description typed per Deal key (a Deal not in it shows header.remarks). */
  dealRemarks?: Record<string, string>;
  onDealRemarkChange?: (key: string, value: string) => void;
  /** Per-SO-Group ship-to picker (Zoho ship-tos of the matched Account). */
  groupShipTo?: GroupShipToControl;
  /** New Deal: Ms_User.UserID of the Sales person picked as Deal Owner ('' = Account Owner). */
  salesUserId?: string;
  onOwnerChange?: (salesUserId: string) => void;
  /** New Deal whose Account Owner is not an active Zoho user, and no Sales picked yet. */
  ownerMissing?: boolean;
}) {
  const multiDate = dateGroups.length > 1;
  const remaining = dateGroups.filter((g) => !groupStatus(g, posts).ok).length;
  // Every group already has its Zoho Sales Order (or the document is POSTED): Send stays disabled,
  // and the bar says why instead of showing a greyed-out button with no reason.
  const allSent = posted || (dateGroups.length > 0 && remaining === 0);
  // Which skipped line (by itemNo) currently has the "Ask AI to suggest a match" popup open.
  const [aiMatchItemNo, setAiMatchItemNo] = useState<string | null>(null);

  const dealHasMaterials = (itemNo: string) => !!dealOfItem(itemNo)?.items.some((it) => it.materialId);
  const dealParts = preview?.deals ?? [];
  const multiDeal = dealParts.length > 1;

  // Document fields shown to the AI when suggesting a match for a skipped line -- the same info a
  // person reads off the card (partner code + material name), enriched with qty/unit/price from
  // the document line where available.
  const docFieldsFor = (itemNo: string): CompareField[] => {
    const dl = docLines.find((l) => String(l.itemNo) === itemNo);
    const sk = preview?.skipped.find((s) => String(s.itemNo) === itemNo);
    return [
      { label: 'Partner Material Code', value: dl?.extCode || sk?.extCode || '' },
      { label: 'Material Name (from document)', value: dl?.desc || sk?.desc || '' },
      { label: 'Quantity', value: dl ? String(dl.qty ?? '') : '' },
      { label: 'Unit', value: dl?.uom || '' },
      { label: 'Unit Price', value: dl ? String(dl.price ?? '') : '' },
    ];
  };

  const aiCandidates: CompareCandidateItem<ZohoDealItem>[] = ((aiMatchItemNo ? dealOfItem(aiMatchItemNo)?.items : null) || [])
    .filter((it): it is ZohoDealItem & { materialId: string } => !!it.materialId)
    .map((it) => ({
      id: it.materialId,
      label: it.materialName || it.materialCode || it.materialId,
      fields: [
        { label: 'Material Name', value: it.materialName },
        { label: 'Material Code', value: it.materialCode },
        { label: 'Material Description', value: it.materialDescription },
        { label: 'Material Group', value: it.materialGroup },
        { label: 'Unit', value: it.unit },
      ],
      raw: it,
    }));

  const renderItemRow = (l: ZohoSalesOrderPreview['lines'][number]) => {
                  const key = String(l.itemNo);
                  const e = lineEdits[key] || { quantity: '', unitPrice: '', unit: '', description: '' };
                  return (
                    <tr key={key}>
                      <td>{key}</td>
                      <td>
                        <b>{l.materialCode || l.materialName || '—'}</b>
                        {l.materialName && l.materialCode && <div className="hint">{l.materialName}</div>}
                      </td>
                      <td>
                        <input type="text" value={e.quantity} disabled={posted} onChange={(ev) => onLineChange(l.itemNo, { quantity: ev.target.value })} style={{ width: 90 }} />
                      </td>
                      <td>
                        <input type="text" value={e.unit} disabled={posted} onChange={(ev) => onLineChange(l.itemNo, { unit: ev.target.value })} style={{ width: 80 }} />
                      </td>
                      <td>
                        <input type="text" value={e.unitPrice} disabled={posted} onChange={(ev) => onLineChange(l.itemNo, { unitPrice: ev.target.value })} style={{ width: 110 }} />
                      </td>
                      <td style={{ whiteSpace: 'nowrap', fontVariantNumeric: 'tabular-nums' }}>
                        {l.lastPrice != null ? (() => {
                          const typed = Number(String(e.unitPrice).replace(/,/g, ''));
                          const differs = e.unitPrice.trim() !== '' && Number.isFinite(typed) && Math.abs(typed - l.lastPrice) > 0.005;
                          return (
                            <span title={[l.lastPriceSo && `From ${l.lastPriceSo}`, l.lastPriceDate && `updated ${formatDate(l.lastPriceDate)}`].filter(Boolean).join(', ')}>
                              <b style={{ color: differs ? 'var(--orange)' : undefined }}>
                                {l.lastPrice.toLocaleString('en-US', { maximumFractionDigits: 2 })}
                              </b>
                              {differs && <i className="fa-solid fa-triangle-exclamation" style={{ color: 'var(--orange)', marginLeft: 4 }} title="Unit Price differs from the last price" />}
                              {l.lastPriceDate && <div className="hint" style={{ fontSize: 14 }}>{formatDate(l.lastPriceDate)}</div>}
                            </span>
                          );
                        })() : <span className="hint">—</span>}
                      </td>
                      <td style={{ whiteSpace: 'nowrap' }}>
                        {l.conversionRatio && l.subUnit ? (
                          <span className="hint">1 {l.unit || 'unit'} = {l.conversionRatio} {l.subUnit}</span>
                        ) : (
                          <span className="hint">—</span>
                        )}
                      </td>
                    </tr>
                  );
                };

  // Ordered Items cut the same way as the Sales Orders (dateGroups = SO Group + delivery date).
  const orderedItemGroups = (() => {
    if (!preview) return [];
    const used = new Set<string>();
    const out = dateGroups.map((g, i) => {
      const nos = new Set(g.itemNos.map(String));
      const lines = preview.lines.filter((l) => nos.has(String(l.itemNo)));
      lines.forEach((l) => used.add(String(l.itemNo)));
      return {
        key: g.key,
        deal: g.deal as string | undefined,
        title: `Sales Order ${i + 1} of ${dateGroups.length}${multiDeal && g.deal ? ` · Deal ${dealLabel(g.deal)}` : ''} · Delivery ${g.date}${dateGroups.some((x) => x.soGroup !== '1') ? ` · SO ${g.soGroup}` : ''} · ${lines.length} item${lines.length === 1 ? '' : 's'}`,
        lines,
      };
    }).filter((g) => g.lines.length > 0);
    const rest = preview.lines.filter((l) => !used.has(String(l.itemNo)));
    if (rest.length) out.push({ key: 'other', deal: undefined, title: 'Other items', lines: rest });
    return out;
  })();

  // Several Deals (19 Nov 2026): each Deal's Description sits under its first Sales Order in Ordered Items
  // (Megachem: next to the order it goes to). A Deal's later Sales Orders say they share it.
  const dealTitleOf = (key: string) => {
    const deal = dealByKey?.(key) ?? null;
    return deal
      ? [deal.dealNumber, deal.deliveryDate && formatDate(deal.deliveryDate)].filter(Boolean).join(' · ') || dealLabel(key)
      : dealLabel(key);
  };
  const perDealRemarks = multiDeal && !!onDealRemarkChange;
  const firstGroupOfDeal = new Map<string, string>();
  orderedItemGroups.forEach((g, i) => { if (g.deal && !firstGroupOfDeal.has(g.deal)) firstGroupOfDeal.set(g.deal, `Sales Order ${i + 1}`); });
  const dealRemarkRow = (g: { key: string; deal?: string }) => {
    if (!perDealRemarks || !g.deal) return null;
    const first = orderedItemGroups.find((x) => x.deal === g.deal)?.key === g.key;
    return (
      <tr>
        <td colSpan={7} style={{ padding: '8px 12px' }}>
          {first ? (
            <div className="f" style={{ margin: 0 }}>
              <label>Description — {dealTitleOf(g.deal)}</label>
              <textarea rows={2} value={dealRemarks[g.deal] ?? header.remarks} disabled={posted}
                onChange={(e) => onDealRemarkChange!(g.deal!, e.target.value)} />
            </div>
          ) : (
            <span className="hint">Description: same as {firstGroupOfDeal.get(g.deal)} (Deal {dealTitleOf(g.deal)})</span>
          )}
        </td>
      </tr>
    );
  };

  const body = () => {
    if (!selectedDealId) return <p className="hint">Group the items into Deals on the Deal (Zoho) card in Step 2 — the Sales Orders to send will appear here.</p>;
    if (loading) return <p className="hint">Reading the Sales Order from Zoho CRM…</p>;
    if (error) return <p className="hint" style={{ color: 'var(--red, #c0392b)' }}>Could not build the Sales Order: {error}</p>;
    if (!preview) return <p className="hint">No Sales Order data yet.</p>;

    return (
      <>
        {multiDeal ? null : (
          <p className="hint">
            This will be created in Zoho CRM as a <b>Sales Order</b>, linked to{' '}
            {preview.flow?.newDeal ? <>a new Deal <b>{preview.dealName || 'Name will be calculated from the matched Materials'}</b></> : <>Deal <b>{preview.dealName}</b></>}.
          </p>
        )}

        {/* New Deal: pick the Sales (Deal Owner) here; an existing Deal shows its own Owner. */}
        {preview.flow?.newDeal && onOwnerChange
          ? <DealOwnerPicker preview={preview} salesUserId={salesUserId} onChange={onOwnerChange} posted={posted} />
          : <DealOwnerPreview preview={preview} />}
        {multiDeal ? (
          // One PO, several Deals (16 Nov 2026): one compact row per Deal instead of three cards each.
          <DealFlowTable parts={dealParts} dealByKey={dealByKey} dealLabel={dealLabel} dateGroups={dateGroups}
            forecast={newDealForecast} onForecast={onNewDealForecast} disabled={posted} />
        ) : preview.flow && (
          // Zoho chain Deal -> Quote -> Sales Order (27 Oct 2026): what Send does, one step per card.
          <ZohoFlowSteps flow={preview.flow} dealName={preview.dealName} salesOrders={Math.max(1, dateGroups.length)}
            forecast={newDealForecast[dealParts[0]?.key ?? '']} disabled={posted}
            onForecast={onNewDealForecast && dealParts[0] ? (v) => onNewDealForecast(dealParts[0].key, v) : undefined} />
        )}


        <p className="hint" style={{ margin: '4px 0 12px' }}>
          Fields left blank fall back to the linked Deal&apos;s saved value — they are not sent empty.
          The greyed text in a field is what will be sent to Zoho if you leave it blank.
        </p>
        <div className="grid">
          <div className="f">
            <label>Subject</label>
            {multiDeal ? (
              // Several Deals: each Quote / Sales Order is named after its own Deal.
              <input type="text" value="Each Deal's own name" disabled readOnly />
            ) : (
              <input type="text" value={header.subject} placeholder={preview.subject || ''} disabled={posted} maxLength={50} onChange={(e) => onHeaderChange({ subject: e.target.value })} />
            )}
          </div>
          <div className="f">
            <label>Customer PO / Customer Ref.</label>
            <input type="text" value={header.customerRef} placeholder={preview.customerRef || ''} disabled={posted} onChange={(e) => onHeaderChange({ customerRef: e.target.value })} />
          </div>
          <div className="f f-check">
            <label>Delivery Date</label>
            {multiDate ? (
              // Each Sales Order is sent with its own group's date (see the group panel below).
              <input type="text" value={`Per Sales Order: ${[...new Set(dateGroups.map((g) => g.date))].join(', ')}`} disabled readOnly />
            ) : (
              <DateInput
                value={header.deliveryDate || preview.deliveryDate || ''}
                disabled={posted}
                onChange={(v) => onHeaderChange({ deliveryDate: v })}
                ariaLabel="Delivery Date"
              />
            )}
          </div>
          <div className="f">
            <label>Payment Terms</label>
            <select value={header.paymentTerms} disabled={posted} onChange={(e) => onHeaderChange({ paymentTerms: e.target.value })}>
              <option value="">{preview.paymentTerms ? `— use Deal default (${preview.paymentTerms}) —` : '— not set —'}</option>
              {preview.paymentTermsOptions.map((o) => (
                <option key={o} value={o}>
                  {o}
                </option>
              ))}
            </select>
          </div>
          <div className="f">
            <label>Payment Currency</label>
            <select value={header.paymentCurrency} disabled={posted} onChange={(e) => onHeaderChange({ paymentCurrency: e.target.value })}>
              <option value="">{preview.paymentCurrency ? `— use Deal default (${preview.paymentCurrency}) —` : '— not set —'}</option>
              {preview.paymentCurrencyOptions.map((o) => (
                <option key={o} value={o}>
                  {o}
                </option>
              ))}
            </select>
          </div>
          <div className="f">
            <label>Incoterms</label>
            <input type="text" value={header.incoterms} placeholder={preview.incoterms || ''} disabled={posted} onChange={(e) => onHeaderChange({ incoterms: e.target.value })} />
          </div>
          <div className="f">
            <label>Tax ID</label>
            <input type="text" value={header.taxId} placeholder={preview.taxId || ''} disabled={posted} onChange={(e) => onHeaderChange({ taxId: e.target.value })} />
          </div>
          {multiDeal && onDealRemarkChange ? (
            // Several Deals: one Description per Deal, under its Sales Order in Ordered Items below.
            <div className="f f-check" style={{ gridColumn: '1 / -1' }}>
              <label>Description</label>
              <span className="hint">Set per Deal under each Sales Order in Ordered Items below</span>
            </div>
          ) : (
          <div className="f f-check" style={{ gridColumn: '1 / -1' }}>
            {/* Document Remark is sent to Zoho's Sales Order header Description. */}
            <label>Description</label>
            <textarea rows={2} value={header.remarks} disabled={posted} onChange={(e) => onHeaderChange({ remarks: e.target.value })} />
          </div>
          )}
          {preview.accountCode && (
            <div className="f f-check">
              <label>Account Code (linked)</label>
              <input type="text" value={preview.accountCode} disabled readOnly />
            </div>
          )}
        </div>

        <p className="sec-title" style={{ marginTop: 24 }}>
          ORDERED ITEMS ({preview.lines.length})
        </p>
        {preview.lastPriceError && (
          <p className="hint" style={{ color: 'var(--orange)' }}>
            <i className="fa-solid fa-triangle-exclamation" /> Last Price could not be read from Zoho: {preview.lastPriceError}
          </p>
        )}
        {preview.lines.length === 0 ? (
          <p className="hint">No document line is matched to a Deal Item yet — resolve the skipped lines below first.</p>
        ) : (
          <div className="tw" style={{ overflowX: 'auto' }}>
            <table>
              <thead>
                <tr>
                  <th>Line</th>
                  <th>Material (Zoho)</th>
                  <th>Qty</th>
                  <th>Unit</th>
                  <th>Unit Price</th>
                  <th title="This customer's last price for the Material (Zoho Last Prices)">Last Price</th>
                  <th>Conversion</th>
                </tr>
              </thead>
              <tbody>
                {/* Several Sales Orders (SO Group / delivery date): the items are listed under the Sales
                    Order each one goes to, instead of as one combined list. */}
                {(multiDate ? orderedItemGroups : [{ key: 'all', deal: undefined, title: '', lines: preview.lines }]).map((g) => (
                  <Fragment key={g.key}>
                    {g.title && (
                      <tr>
                        <td colSpan={7} style={{ background: 'var(--g100)', fontWeight: 700, padding: '8px 12px' }}>
                          {g.title}
                        </td>
                      </tr>
                    )}
                    {dealRemarkRow(g)}
                    {g.lines.map(renderItemRow)}
                  </Fragment>
                ))}
              </tbody>
            </table>
          </div>
        )}

        {preview.skipped.length > 0 && (
          <div style={{ marginTop: 16 }}>
            <p className="sec-title">NOT MATCHED ({preview.skipped.length}) — won't be sent unless you match them</p>
            <div className="tw" style={{ overflowX: 'auto' }}>
              <table>
                <thead>
                  <tr>
                    <th>Line</th>
                    <th>Partner Code</th>
                    <th>Material Name (from document)</th>
                    <th>Reason</th>
                    <th></th>
                  </tr>
                </thead>
                <tbody>
                  {preview.skipped.map((sk, i) => {
                    const key = String(sk.itemNo ?? i + 1);
                    return (
                      <tr key={key}>
                        <td>{key}</td>
                        <td>{sk.extCode || '—'}</td>
                        <td>{sk.desc || '—'}</td>
                        <td className="hint">{sk.reason}</td>
                        <td>
                          {dealHasMaterials(key) && !posted && (
                            <button className="btn sm" onClick={() => setAiMatchItemNo(key)}>
                              <i className="fa-solid fa-wand-magic-sparkles" /> Ask AI to match
                            </button>
                          )}
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          </div>
        )}

        {result && !result.success && (
          <div className="hint" style={{ color: 'var(--red, #c0392b)', marginTop: 16 }}>
            Zoho did not accept the record: {result.message || 'Unknown error'}
          </div>
        )}
        {result && result.success && (
          <div className="badge b-ok" style={{ marginTop: 16 }}>
            <i className="fa-solid fa-check" /> Sent — {result.groups && result.groups.length > 1 ? `${result.groups.length} Sales Orders` : 'Sales Order'} created in Zoho CRM ({result.linesSent} line(s))
          </div>
        )}

        <SoGroupPanel groups={dateGroups} posts={posts} target="Zoho CRM" shipTo={groupShipTo}
          itemLabel={(no) => {
            const pl = preview?.lines.find((l) => String(l.itemNo) === String(no));
            const dl = docLines.find((l) => String(l.itemNo) === String(no));
            const code = pl?.materialCode || dl?.materialCode || '';
            const name = pl?.materialName || dl?.desc || '';
            return [code, name].filter(Boolean).join(' — ');
          }} />

        <div className="so-send">
          {/* Status / reason on the left, actions on the right (Send last = primary at the end). */}
          {(result?.success || shipToMissing || ownerMissing || forecastMissing || multiDate || preview.lines.length === 0 || preview.skipped.length > 0 || allSent || SEND_DISABLED) && (
          <div className="so-send-status">
            <b>
              {result?.success
                ? 'Sent to Zoho CRM'
                : SEND_DISABLED
                ? 'Sending is switched off'
                : allSent
                ? 'Already sent to Zoho CRM'
                : shipToMissing
                ? 'Ship-to required'
                : ownerMissing
                ? 'Sales person required'
                : forecastMissing
                ? 'Forecast required for the new Deal'
                : preview.lines.length === 0
                  ? 'Nothing to send yet'
                  : multiDate
                    ? `${remaining} Sales Order${remaining === 1 ? '' : 's'} to send`
                    : `${preview.skipped.length} line${preview.skipped.length === 1 ? '' : 's'} not matched`}
            </b>
            <div className="hint">
              {SEND_DISABLED && !result?.success
                ? 'SEND_DISABLED is on in constants/flags.ts'
                : allSent && !result?.success
                ? `Every Sales Order of this document is already in Zoho (${posts.filter((p) => p.posted).map((p) => p.docNo).filter(Boolean).join(', ') || 'see the Sales Orders table'}) — nothing left to send`
                : shipToMissing && !result?.success
                ? multiDate
                  ? 'Select a Ship-to in the mapping cards above, or one for each SO Group in the Sales Orders table'
                  : 'Select a Ship-to in the mapping cards above before sending'
                : ownerMissing && !result?.success
                ? 'Select a Sales above'
                : forecastMissing && !result?.success
                ? 'Choose Forecast Yes or No for each new Deal (Deal step above)'
                : multiDate
                ? `${dateGroups.length} Sales Orders (by SO Group and delivery date) — same Customer PO, each with its own Delivery Date and Ship-to`
                : preview.lines.length === 0
                ? 'Match at least one line before sending'
                : preview.skipped.length
                  ? `Not sent: line ${preview.skipped.map((sk, i) => String(sk.itemNo ?? i + 1)).join(', ')}`
                  : ''}
            </div>
          </div>
          )}
          <div className="row" style={{ gap: 12, marginLeft: 'auto' }}>
            <button className="btn" onClick={onViewPayload} disabled={preview.lines.length === 0}>
              {'{}'} View payload
            </button>
            <button
              className="btn success"
              onClick={onSend}
              disabled={SEND_DISABLED || sending || posted || shipToMissing || ownerMissing || forecastMissing || preview.lines.length === 0 || !!result?.success || remaining === 0}
            >
              {sending
                ? 'Sending…'
                : result?.success
                  ? 'Sent ✓'
                  : multiDate
                    ? `Send ${remaining} Sales Order${remaining === 1 ? '' : 's'} to Zoho CRM`
                    : 'Send to Zoho CRM'}
            </button>
          </div>
        </div>
      </>
    );
  };

  return (
    <>
      <div className="card">
        <div className="card-h">
          <h2>Sales Order summary to send to Zoho CRM</h2>
          <div className="sp" />
          {preview && preview.skipped.length > 0 && (
            <span className="badge b-warn">{preview.skipped.length} line(s) not matched</span>
          )}
        </div>
        <div className="card-b">{body()}</div>
      </div>

      <CompareModal<ZohoDealItem>
        open={aiMatchItemNo != null}
        onClose={() => setAiMatchItemNo(null)}
        title={`AI Suggested Match — Line ${aiMatchItemNo ?? ''}`}
        docFields={aiMatchItemNo ? docFieldsFor(aiMatchItemNo) : []}
        candidates={aiCandidates}
        providers={providers}
        onUse={(item) => {
          if (aiMatchItemNo) onUseAiMatch(aiMatchItemNo, item);
          setAiMatchItemNo(null);
        }}
      />
    </>
  );
}

export type ZohoSalesOrderEditorHeader = {
  subject: string;
  customerRef: string;
  deliveryDate: string;
  paymentTerms: string;
  paymentCurrency: string;
  incoterms: string;
  taxId: string;
  remarks: string;
};

/* Deal -> Quote -> Sales Order as three numbered steps: for each, whether Send uses an existing
   record or creates a new one, which record, and why. */
/* What Send does when the PO holds several Deals (16 Nov 2026, Megachem: "too much text"): one row per Deal
   -- Deal (Deal Number · delivery date; the name is the tooltip), Owner, Forecast (Yes / No to pick for a new
   Deal), Quote (use / new; the reason is the tooltip), Sales Orders. */
function DealFlowTable({ parts, dealByKey, dealLabel, dateGroups, forecast, onForecast, disabled }: {
  parts: NonNullable<ZohoSalesOrderPreview['deals']>;
  dealByKey?: (key: string) => ZohoDeal | null;
  dealLabel: (key: string) => string;
  dateGroups: DeliveryDateGroup[];
  forecast: Record<string, 'Yes' | 'No'>;
  onForecast?: (key: string, value: 'Yes' | 'No') => void;
  disabled?: boolean;
}) {
  // Same colours as the step cards (ZohoFlowSteps): use = green, create = blue, unknown = orange.
  const tag = (text: string, kind: 'use' | 'create' | 'unknown') => {
    const c = kind === 'use' ? { bg: 'var(--brand-soft)', fg: 'var(--brand)' }
      : kind === 'create' ? { bg: 'var(--info-bg)', fg: 'var(--info)' }
      : { bg: 'var(--orange-bg)', fg: 'var(--orange)' };
    return (
      <span style={{ display: 'inline-block', padding: '1px 8px', borderRadius: 999, fontSize: 14, fontWeight: 700,
        background: c.bg, color: c.fg, marginRight: 8 }}>{text}</span>
    );
  };
  return (
    <div style={{ overflowX: 'auto', margin: '8px 0 16px' }}>
      <table style={{ minWidth: 640 }}>
        <thead>
          <tr><th>Deal</th><th>Owner</th><th>Forecast</th><th>Quote</th><th style={{ textAlign: 'right' }}>Sales Orders</th></tr>
        </thead>
        <tbody>
          {parts.map((p) => {
            const deal = dealByKey?.(p.key) ?? null;
            const title = deal
              ? [deal.dealNumber, deal.deliveryDate && formatDate(deal.deliveryDate)].filter(Boolean).join(' · ') || p.dealName
              : dealLabel(p.key);
            const picked = forecast[p.key];
            const sos = Math.max(1, dateGroups.filter((g) => g.deal === p.key).length);
            return (
              <tr key={p.key}>
                <td title={p.dealName}>
                  {tag(p.flow.newDeal ? 'New' : 'Existing', p.flow.newDeal ? 'create' : 'use')}
                  <b>{title}</b>
                </td>
                <td className="hint">{p.ownerName || '—'}</td>
                <td>
                  {p.flow.newDeal && onForecast ? (
                    <span role="radiogroup" aria-label={`Forecast of ${title}`} style={{ display: 'inline-flex', gap: 4 }}>
                      {(['Yes', 'No'] as const).map((v) => (
                        <button key={v} type="button" role="radio" aria-checked={picked === v} disabled={disabled}
                          className={'btn sm' + (picked === v ? ' on' : '')}
                          style={!picked ? { borderColor: '#b91c1c' } : undefined}
                          onClick={() => onForecast(p.key, v)}>{v}</button>
                      ))}
                    </span>
                  ) : p.flow.wcd ? <span className="badge b-ok">WCD</span> : <span className="hint">No</span>}
                </td>
                <td title={p.flow.quoteReason}>
                  {p.flow.quoteAction === 'use'
                    ? <>{tag('Use', 'use')}{p.flow.quoteNo}</>
                    : p.flow.quoteAction === 'create' ? tag('New', 'create') : tag('Check', 'unknown')}
                </td>
                <td style={{ textAlign: 'right' }}>{sos}</td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

function ZohoFlowSteps({ flow, dealName, salesOrders, forecast, onForecast, disabled }: {
  flow: ZohoSalesOrderFlow; dealName: string; salesOrders: number;
  /** New Deal: the Forecast Status CS chose ('' / undefined = not chosen yet). */
  forecast?: 'Yes' | 'No';
  onForecast?: (value: 'Yes' | 'No') => void;
  disabled?: boolean;
}) {
  type Step = { title: string; action: string; kind: 'use' | 'create' | 'unknown'; main: string; sub: string; extra?: ReactNode };
  // New Deal: CS picks Forecast Yes (WCD) or No before Send -- no default (16 Nov 2026).
  const forecastPicker = flow.newDeal && onForecast ? (
    <div role="radiogroup" aria-label="Forecast Status of the new Deal" style={{ display: 'flex', flexWrap: 'wrap', gap: 8, alignItems: 'center' }}>
      {(['Yes', 'No'] as const).map((v) => (
        <button key={v} type="button" role="radio" aria-checked={forecast === v} disabled={disabled}
          className={'btn sm' + (forecast === v ? ' on' : '')} onClick={() => onForecast(v)}>
          <i className={forecast === v ? 'fa-solid fa-circle-dot' : 'fa-regular fa-circle'} />{' '}
          {v === 'Yes' ? 'Forecast (WCD) — Yes' : 'No forecast — No'}
        </button>
      ))}
      {!forecast && <span style={{ color: '#b91c1c', fontWeight: 700, fontSize: 14 }}>Choose Forecast before sending</span>}
    </div>
  ) : undefined;
  const steps: Step[] = [
    {
      title: 'Deal',
      action: flow.newDeal ? 'Create new' : 'Use existing',
      kind: flow.newDeal ? 'create' : 'use',
      main: dealName || 'New Deal for this customer',
      sub: flow.newDeal
        ? forecast
          ? `${forecast === 'Yes' ? 'Forecast (WCD)' : 'No forecast'} · Forecast Status ${forecast} · Stage “Draft/Qualification”`
          : 'Stage “Draft/Qualification” · choose the Forecast Status'
        : flow.wcd ? 'Forecast (WCD) · Forecast Status Yes' : `No forecast · Forecast Status ${flow.forecastStatus || 'blank'}`,
      extra: forecastPicker,
    },
    {
      title: 'Quote',
      action: flow.quoteAction === 'use' ? 'Use existing' : flow.quoteAction === 'create' ? 'Create new' : 'Could not check',
      kind: flow.quoteAction,
      main: flow.quoteAction === 'use'
        ? `${flow.quoteNo || 'Existing Quote'}${flow.quoteStage ? ` (${flow.quoteStage})` : ''}`
        : flow.quoteAction === 'create' ? 'New Quote · Stage Open · every line of this document' : 'Zoho CRM could not be read',
      sub: flow.quoteReason,
    },
    {
      title: 'Sales Order',
      action: 'Create new',
      kind: 'create',
      main: salesOrders > 1 ? `${salesOrders} Sales Orders` : '1 Sales Order',
      sub: 'Linked to the Deal and the Quote above',
    },
  ];
  const tone = (k: Step['kind']) =>
    k === 'use' ? { bg: 'var(--brand-soft)', fg: 'var(--brand)', icon: 'fa-link' }
    : k === 'create' ? { bg: 'var(--info-bg)', fg: 'var(--info)', icon: 'fa-plus' }
    : { bg: 'var(--orange-bg)', fg: 'var(--orange)', icon: 'fa-triangle-exclamation' };
  return (
    <ol aria-label="What Send does in Zoho CRM" style={{
      listStyle: 'none', margin: '8px 0 16px', padding: 0, display: 'grid', gap: 12,
      gridTemplateColumns: 'repeat(auto-fit, minmax(220px, 1fr))',
    }}>
      {steps.map((st, i) => {
        const t = tone(st.kind);
        return (
          <li key={st.title} style={{
            border: '1px solid var(--g300)', borderRadius: 12, padding: '12px 16px', background: 'var(--card)',
            display: 'flex', flexDirection: 'column', gap: 8, minWidth: 0, position: 'relative',
          }}>
            <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
              <span style={{
                width: 24, height: 24, borderRadius: '50%', display: 'grid', placeItems: 'center', fontWeight: 700, fontSize: 14,
                background: 'var(--g200)', color: 'var(--g1600, inherit)',
              }}>{i + 1}</span>
              <b style={{ fontSize: 16 }}>{st.title}</b>
              <div className="sp" style={{ flex: 1 }} />
              <span style={{
                display: 'inline-flex', alignItems: 'center', gap: 8, padding: '3px 12px', borderRadius: 999,
                fontSize: 14, fontWeight: 700, background: t.bg, color: t.fg,
              }}>
                <i className={'fa-solid ' + t.icon} /> {st.action}
              </span>
            </div>
            <div style={{ fontWeight: 600, overflowWrap: 'anywhere' }}>{st.main}</div>
            <div className="hint">{st.sub}</div>
            {st.extra}
            {i < steps.length - 1 && (
              <span aria-hidden="true" className="hint" style={{ position: 'absolute', right: -9, top: 14, fontSize: 14 }}>›</span>
            )}
          </li>
        );
      })}
    </ol>
  );
}
