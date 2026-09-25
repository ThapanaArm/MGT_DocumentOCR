import { Fragment, useEffect, useState } from 'react';
import Modal, { ModalHeader } from '../Modal';
import { MASTER_DEF, M_LABEL } from '../../constants/fields';
import { createMaster, deleteMaster, updateMaster, type MasterRow, type MastersData } from '../../api/masters';
import { searchSapBusinessPartner, type SapBusinessPartner, type SapLastPrice } from '../../api/sap';
import { searchZohoAccount, type ZohoAccount } from '../../api/zoho';
import { useAppState } from '../../state/AppState';
import type { Dupe } from '../../utils/dupes';

/* Ports editRow()/saveRow()/useDupe() — the add/edit master row modal, shared
   by MasterPage and the document quick-add flow. */

export interface MasterEditState {
  tab: string;
  rowKey: string | number | null;
  prefill?: MasterRow;
  dupes?: Dupe[];
  /** called with the saved row's key after a successful save */
  onSaved?: (savedKey: string) => void | Promise<void>;
  /** called when the user picks an existing duplicate instead */
  onUseDupe?: (code: string) => void;
  /** GLC material-confirm popup only (tab 'uoms', opened from DocumentPage's useSapMaterial): the
   *  last actual price SAP billed THIS customer for THIS material, fetched live (see
   *  getSapLastPrice). Shown as read-only reference context right beside the Material field —
   *  never written to the saved UomConversion row (that table has no customer/price columns).
   *  undefined = not applicable; null = looked up but SAP had no prior billing line for this pair. */
  lastPrice?: SapLastPrice | null;
}

export default function MasterEditModal({
  state,
  masters,
  onClose,
  afterSave,
  isMgt,
}: {
  state: MasterEditState | null;
  masters: MastersData;
  onClose: () => void;
  afterSave: () => void | Promise<void>;
  /** true when the signed-in user's company is MGT — the quick-add "Found in ..." search below
   *  looks up Zoho CRM instead of SAP (see DocumentPage / MasterPage, which read
   *  primaryCompany.companyCode via AppLayout's Outlet context). Defaults to false (SAP), same
   *  as before this prop existed. */
  isMgt?: boolean;
}) {
  const { guard, showToast } = useAppState();
  const [form, setForm] = useState<MasterRow>({});
  const [saving, setSaving] = useState(false);
  useEffect(() => {
    if (!state) return;
    const def = MASTER_DEF[state.tab];
    const existing = state.rowKey != null
      ? { ...(masters[state.tab]?.find((x) => String(x[def.key]) === String(state.rowKey)) || {}), ...(state.prefill || {}) }
      : state.prefill || {};
    setForm({ SalesOrg: isMgt ? '1000' : '2000', IsActive: 1, Isactive: 1, ...existing });
  }, [state, masters, isMgt]);

  // Live customer lookup — only for a brand-new Customer row (quick-add flow), seeded with the
  // document's customer name. Search is best-effort: any failure (backend not configured yet,
  // network error, ...) just shows nothing here, it never blocks the modal. Which system gets
  // searched depends on isMgt: MGT -> Zoho CRM, everyone else (Green Leaf) -> SAP, matching the
  // same company split used in MappingCards' live "Data from SAP"/"Data from Zoho CRM" panel.
  const [sapResults, setSapResults] = useState<SapBusinessPartner[]>([]);
  const [sapLoading, setSapLoading] = useState(false);
  const [sapError, setSapError] = useState<string | null>(null);
  const [zohoResults, setZohoResults] = useState<ZohoAccount[]>([]);
  const [zohoLoading, setZohoLoading] = useState(false);
  const [zohoError, setZohoError] = useState<string | null>(null);
  // Tax ID is an exact match (one company = one Tax ID) so it's tried first; name is a fuzzy
  // fallback the backend only uses if the Tax ID search comes up empty (or wasn't read at all).
  const customerLookup =
    state?.tab === 'customers' && state.rowKey == null
      ? {
          name: (state.prefill?.CompanyName || '').toString().trim(),
          taxId: (state.prefill?.TaxId || '').toString().trim(),
          companyCode: (state.prefill?.SalesOrg || '').toString().trim(),
        }
      : null;
  const customerLookupKey = customerLookup ? customerLookup.taxId + '|' + customerLookup.name : '';

  useEffect(() => {
    if (isMgt || !customerLookup || (!customerLookup.taxId && !customerLookup.name)) {
      setSapResults([]);
      setSapError(null);
      return;
    }
    let cancelled = false;
    setSapLoading(true);
    setSapError(null);
    searchSapBusinessPartner(customerLookup)
      .then((r) => {
        if (cancelled) return;
        if (!Array.isArray(r?.results)) {
          setSapResults([]);
          setSapError('Unexpected response from the server (is the backend up to date?)');
          return;
        }
        setSapResults(r.results);
      })
      .catch((e) => {
        if (!cancelled) {
          setSapResults([]);
          setSapError(e?.message || 'SAP search failed');
        }
      })
      .finally(() => {
        if (!cancelled) setSapLoading(false);
      });
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [customerLookupKey, isMgt]);

  useEffect(() => {
    if (!isMgt || !customerLookup || (!customerLookup.taxId && !customerLookup.name)) {
      setZohoResults([]);
      setZohoError(null);
      return;
    }
    let cancelled = false;
    setZohoLoading(true);
    setZohoError(null);
    searchZohoAccount(customerLookup)
      .then((r) => {
        if (cancelled) return;
        if (!Array.isArray(r?.results)) {
          setZohoResults([]);
          setZohoError('Unexpected response from the server (is the backend up to date?)');
          return;
        }
        setZohoResults(r.results);
      })
      .catch((e) => {
        if (!cancelled) {
          setZohoResults([]);
          setZohoError(e?.message || 'Zoho CRM search failed');
        }
      })
      .finally(() => {
        if (!cancelled) setZohoLoading(false);
      });
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [customerLookupKey, isMgt]);

  if (!state) return null;
  const def = MASTER_DEF[state.tab];
  const visibleCols = state.tab === 'uoms' && isMgt
    ? def.cols.filter((c) => c.k !== 'SapUomIso')
    : def.cols;
  // uoms labels read the same for MGT and GLC now (Document Unit / Order Unit / Factor), so the
  // old per-company "Zoho Unit" relabel is gone — labels come straight from MASTER_DEF.
  const fieldLabel = (c: (typeof def.cols)[number]) => c.l;

  // Live worked example under the uoms form: shows what the row being typed will do to a line.
  const uomPreview = (() => {
    const ext = (form.ExtUom ?? '').toString().trim() || 'document unit';
    const sap = (form.SapUom ?? '').toString().trim().toUpperCase();
    const factor = Number(form.Factor);
    if (!sap) return 'Fill in the Order Unit to see how a line would be sent.';
    if (!Number.isFinite(factor) || factor <= 0) return `1 ${ext} = ? ${sap} — fill in the Factor.`;
    // Matches MappingEngine.ConvertUom: sapQty = docQty * Factor (1 document unit = Factor order units).
    if (factor === 1) return `A line that says 100 ${ext} is sent as 100 ${sap}.`;
    const qty = Number((100 * factor).toFixed(6));
    return `1 ${ext} = ${factor} ${sap}, so a line that says 100 ${ext} is sent as ${qty} ${sap}.`;
  })();
  const editing = state.rowKey != null;
  const existing = editing
    ? { ...(masters[state.tab].find((x) => String(x[def.key]) === String(state.rowKey)) || {}), ...(state.prefill || {}) }
    : state.prefill || {};

  const setField = (k: string, v: string) => setForm((f) => ({ ...f, [k]: v }));

  // Grouping for the redesigned modal: Status pill on top, key fields above the fold, and the
  // rarely-touched SAP-derived fields tucked into a collapsible section.
  const hasSources = visibleCols.some((c) => c.source);
  const systemCols = visibleCols.filter((c) => c.source === 'system');
  const docCols = visibleCols.filter((c) => c.source === 'document');
  const extKeyCols = visibleCols.filter((c) => c.source === 'external' && !c.derived);
  const extDerivedCols = visibleCols.filter((c) => c.source === 'external' && c.derived);
  // The SAP/Zoho code column (if any). An existing row with it blank is an incomplete mapping.
  const sapCol = def.cols.find((c) => c.sap);
  const sapMissing = editing && !!sapCol && !String(form[sapCol.k] ?? '').trim();

  const renderField = (c: (typeof def.cols)[number]) => (
    <Fragment key={c.k}>
      <div className="f">
        <label htmlFor={'master-' + c.k}>
          {fieldLabel(c)}{isColRequired(c) ? ' *' : ''}
          {(c.help || c.source) && (
            <i
              className="fa-regular fa-circle-question master-field-info"
              title={[c.help, c.k].filter(Boolean).join(' · ')}
            />
          )}
        </label>
        {c.k === 'SalesOrg' ? (
          <select id={'master-' + c.k} value={form[c.k] ?? ''} onChange={(e) => { setField(c.k, e.target.value); if (state!.tab === 'custmaterials' || state!.tab === 'shiptos') setField('CustomerCode', ''); }}>
            {state!.tab === 'uoms' && <option value="">— All companies —</option>}
            <option value="1000">1000 — MGT</option><option value="2000">2000 — GLC</option>
          </select>
        ) : c.source === 'system' ? (
          <select id={'master-' + c.k} value={Number(form[c.k] ?? 1)} onChange={(e) => setField(c.k, e.target.value)}><option value="1">1 — Active</option><option value="0">0 — Inactive</option></select>
        ) : c.ref ? (
          <select id={'master-' + c.k} value={form[c.k] ?? ''} onChange={(e) => setField(c.k, e.target.value)}>
            {!c.blank && <option value="">— Select —</option>}
            {c.blank && <option value="">— All materials (global rule) —</option>}
            {(masters[c.ref] || []).filter((o) => (o.IsActive == null || !!Number(o.IsActive)) && (c.ref !== 'customers' || !form.SalesOrg || (state!.tab !== 'custmaterials' && state!.tab !== 'shiptos') || String(o.SalesOrg) === String(form.SalesOrg))).map((o, index) => {
              const vk = MASTER_DEF[c.ref!].matchKey || MASTER_DEF[c.ref!].key;
              const lk = M_LABEL[c.ref!];
              return (
                <option key={index} value={o[vk]}>
                  {o[vk]} — {o[lk]}
                </option>
              );
            })}
          </select>
        ) : (
          <input id={'master-' + c.k} maxLength={c.maxLen} value={form[c.k] ?? ''} onChange={(e) => setField(c.k, e.target.value)} />
        )}
      </div>
      {/* GLC material-confirm popup only (uoms) — reference context beside the Material field, never saved. */}
      {c.k === 'MaterialCode' && state!.tab === 'uoms' && state!.lastPrice !== undefined && (
        <div className="f">
          <label><i className="fa-solid fa-tag" /> Last Price</label>
          {state!.lastPrice ? (
            <input
              type="text"
              disabled
              readOnly
              value={`${state!.lastPrice.pricePerUnit.toLocaleString()} / ${state!.lastPrice.unit || 'unit'}${state!.lastPrice.creationDate ? ` (billed ${state!.lastPrice.creationDate})` : ''}`}
            />
          ) : (
            <input type="text" disabled readOnly value="No prior SAP billing found" />
          )}
          <small className="master-field-help">for reference only, confirm the unit with what Sales told CS to use</small>
        </div>
      )}
    </Fragment>
  );

  // Whether a column is actually required in THIS context. Static `required` from MASTER_DEF, with
  // one company-specific relaxation: for a GLC (non-MGT) Ship-to, the SAP Ship-to code is optional
  // — some GLC delivery locations aren't mapped to / sent to SAP at all, so a Ship-to master can be
  // saved without one. MGT keeps it required.
  const isColRequired = (c: (typeof def.cols)[number]) => {
    if (!c.required) return false;
    if (state.tab === 'shiptos' && !isMgt && c.k === 'SapShipToCode') return false;
    // Editing an existing row: the SAP/Zoho code is no longer forced, so a not-yet-mapped record
    // can still be corrected and saved (it just shows the "incomplete" warning until filled).
    if (state.rowKey != null && c.sap) return false;
    return true;
  };

  async function save() {
    if (saving) return;
    const o: MasterRow = {};
    def.cols.forEach((c) => {
      o[c.k] = c.source === 'system' ? Number(form[c.k] ?? 1) : (form[c.k] ?? '').toString().trim();
    });
    const missing = def.cols.find((c) => isColRequired(c) && !o[c.k]);
    if (missing) { showToast('Please specify ' + missing.l); return; }
    // uoms: SapUom goes straight onto the SAP/Zoho order line, so it has to be a real code — at
    // most 3 chars, no spaces and no "/" (a hand-typed "KG/PC" is exactly what SAP rejected with
    // RequestedQuantityUnit invalid). Factor defaults to 1 (no quantity change).
    if (state!.tab === 'uoms') {
      ['SapUom', 'SapUomIso'].forEach((k) => { o[k] = (o[k] ?? '').toString().trim().toUpperCase(); });
      const bad = ['SapUom', 'SapUomIso'].find((k) => o[k] && (String(o[k]).length > 3 || /[\s/\\]/.test(String(o[k]))));
      if (bad) {
        showToast(`${def.cols.find((c) => c.k === bad)?.l || bad} must be at most 3 characters, with no spaces or "/"`);
        return;
      }
      if (!o.Factor) o.Factor = '1';
      const factor = Number(o.Factor);
      if (!Number.isFinite(factor) || factor <= 0) { showToast('Factor must be a number greater than 0'); return; }
      o.Factor = String(factor);
    }
    if (state!.rowKey == null && !['Id', 'id'].includes(def.key) && !o[def.key]) {
      showToast('Please enter ' + def.cols.find((c) => c.k === def.key)?.l);
      return;
    }
    setSaving(true);
    const ok = await guard(async () => {
      if (state!.rowKey == null) await createMaster(state!.tab, o);
      else await updateMaster(state!.tab, String(state!.rowKey), o);
      return true;
    });
    setSaving(false);
    if (!ok) return;
    if (state!.onSaved) {
      await state!.onSaved(o[def.matchKey || def.key]);
    } else {
      showToast('Master data saved successfully', 'success');
    }
    await afterSave();
    onClose();
  }

  async function del() {
    if (state!.rowKey == null || saving) return;
    if (!window.confirm('Delete this record? This cannot be undone.')) return;
    setSaving(true);
    const ok = await guard(async () => { await deleteMaster(state!.tab, String(state!.rowKey)); return true; });
    setSaving(false);
    if (!ok) return;
    showToast('Record deleted');
    await afterSave();
    onClose();
  }

  function selectDupe(code: string) {
    onClose();
    state!.onUseDupe?.(code);
  }

  // Fills the form from a live SAP match — does NOT save. The person reviews the prefilled
  // fields (and can still edit them) then clicks the normal Save button below to actually
  // create the local Customer master row.
  function selectSapRecord(bp: SapBusinessPartner) {
    setForm((f) => ({
      ...f,
      ComcompyCodeSAP: bp.businessPartnerId,
      CompanyNameSAP: bp.businessPartnerFullName || bp.businessPartnerName || '',
    }));
    showToast('Filled from SAP — review the fields below, then click Save to store this customer');
  }

  // Same idea, MGT side: fill the configured Zoho Account Code and let the person review it.
  function selectZohoRecord(acc: ZohoAccount) {
    if (!acc.accountCode?.trim()) { showToast('This Zoho record has no Account Code'); return; }
    setForm((f) => ({
      ...f,
      ComcompyCodeSAP: acc.accountCode,
      CompanyNameSAP: acc.accountName || '',
    }));
    showToast('Filled from Zoho CRM — review the fields below, then click Save to store this customer');
  }

  const hasDupes = !!state.dupes && state.dupes.length > 0;

  return (
    <Modal open wide onClose={onClose}>
      <ModalHeader
        title={`${state.rowKey == null ? 'Add' : 'Edit'} — ${def.label}`}
        onClose={onClose}
      />
      <div className="card-b">
        {state.tab === 'customers' && state.rowKey == null && !isMgt && (
          <div className="result" style={{ marginBottom: 16 }}>
            <h3><i className="fa-solid fa-building-columns" /> Found in SAP</h3>
            {sapLoading && <p className="hint" style={{ margin: '0 0 10px' }}>Searching SAP…</p>}
            {sapError && (
              <p className="hint" style={{ margin: '0 0 10px' }}>Could not search SAP: {sapError}</p>
            )}
            {!sapLoading && !sapError && sapResults.length === 0 && (
              <p className="hint" style={{ margin: '0 0 10px' }}>
                No match found in SAP yet — fill in the fields below manually.
              </p>
            )}
            {sapResults.length > 1 && (
              <p className="hint" style={{ margin: '0 0 10px' }}>
                Found {sapResults.length} matches in SAP — this Tax ID may cover more than one
                branch/record. Check each Business Partner code in SAP if unsure which is correct.
              </p>
            )}
            {sapResults.length > 0 && (
              <div className="tw">
                <table style={{ minWidth: 'auto' }}>
                  <tbody>
                    {[...sapResults]
                      .sort((a, b) => Number(!!a.businessPartnerIsBlocked) - Number(!!b.businessPartnerIsBlocked))
                      .map((bp) => (
                        <tr key={bp.businessPartnerId}>
                          <td>
                            <b>{bp.businessPartnerId}</b> — {bp.businessPartnerFullName || bp.businessPartnerName}
                            {bp.businessPartnerIsBlocked && (
                              <span className="badge b-fail" style={{ marginLeft: 6 }}>Blocked in SAP</span>
                            )}
                            {(bp.addressCity || bp.addressStreet) && (
                              <div className="hint">
                                {[bp.addressStreet, bp.addressCity].filter(Boolean).join(', ')}
                              </div>
                            )}
                          </td>
                          <td>
                            <button className="btn sm primary" onClick={() => selectSapRecord(bp)}>
                              Use this SAP record
                            </button>
                          </td>
                        </tr>
                      ))}
                  </tbody>
                </table>
              </div>
            )}
          </div>
        )}
        {state.tab === 'customers' && state.rowKey == null && isMgt && (
          <div className="result" style={{ marginBottom: 16 }}>
            <h3><i className="fa-solid fa-building-columns" /> Found in Zoho CRM</h3>
            {zohoLoading && <p className="hint" style={{ margin: '0 0 10px' }}>Searching Zoho CRM…</p>}
            {zohoError && (
              <p className="hint" style={{ margin: '0 0 10px' }}>Could not search Zoho CRM: {zohoError}</p>
            )}
            {!zohoLoading && !zohoError && zohoResults.length === 0 && (
              <p className="hint" style={{ margin: '0 0 10px' }}>
                No match found in Zoho CRM yet — fill in the fields below manually.
              </p>
            )}
            {zohoResults.length > 1 && (
              <p className="hint" style={{ margin: '0 0 10px' }}>
                Found {zohoResults.length} matches in Zoho CRM — this Tax ID may cover more than
                one branch/record. Check the Branch below if unsure which is correct.
              </p>
            )}
            {zohoResults.length > 0 && (
              <div className="tw">
                <table style={{ minWidth: 'auto' }}>
                  <tbody>
                    {zohoResults.map((acc) => (
                      <tr key={acc.accountId}>
                        <td>
                          <b>{acc.accountCode || acc.accountId}</b> — {acc.accountName}
                          {acc.branchName && (
                            <span className="badge b-idle" style={{ marginLeft: 6 }}>{acc.branchName}</span>
                          )}
                          {acc.taxId && <div className="hint">Tax ID: {acc.taxId}</div>}
                        </td>
                        <td>
                          <button className="btn sm primary" onClick={() => selectZohoRecord(acc)}>
                            Use this Zoho record
                          </button>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </div>
        )}
        {hasDupes && (
          <div className="result bad" style={{ marginBottom: 16 }}>
            <h3><i className="fa-solid fa-triangle-exclamation" /> Found {state.dupes!.length} possibly duplicate records</h3>
            <p className="hint" style={{ margin: '0 0 10px' }}>
              Please review before adding a new record — if it is the same record, click "Use this record instead" rather than creating a duplicate
            </p>
            <div className="tw">
              <table style={{ minWidth: 'auto' }}>
                <tbody>
                  {state.dupes!.map((x, i) => (
                    <tr key={i}>
                      <td>
                        <b>{x.row[def.matchKey || def.key]}</b> — {x.row[M_LABEL[state.tab]] || ''}
                      </td>
                      <td>
                        <span className="badge b-warn">{x.reason}</span>
                      </td>
                      <td>
                        <button
                          className="btn sm primary"
                          onClick={() => selectDupe(String(x.row[def.matchKey || def.key]))}
                        >
                          Use this record instead
                        </button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        )}
        {hasSources ? (
          <>
            {sapMissing && (
              <div className="result master-incomplete">
                <i className="fa-solid fa-triangle-exclamation" /> This mapping has no SAP/Zoho code yet — it won’t be matched automatically in Sales Order until you add one below.
              </div>
            )}
            {systemCols.length > 0 && (
              <div className="master-status-bar">
                {systemCols.map((c) => (
                  <label key={c.k} className="master-status-toggle" htmlFor={'master-' + c.k}>
                    <span>{fieldLabel(c)}</span>
                    <select id={'master-' + c.k} value={Number(form[c.k] ?? 1)} onChange={(e) => setField(c.k, e.target.value)}>
                      <option value="1">Active</option><option value="0">Inactive</option>
                    </select>
                  </label>
                ))}
                {editing && <span className="hint master-status-meta">{def.key}: {existing[def.key]} · Updated: {existing.UpdatedAt || '—'}</span>}
              </div>
            )}
            <div className="master-field-groups">
              <section>
                <h3>Customer and document data</h3>
                <div className="grid">{docCols.map(renderField)}</div>
              </section>
              <section>
                <h3>SAP / Zoho data</h3>
                <div className="grid">{extKeyCols.map(renderField)}</div>
                {extDerivedCols.length > 0 && (
                  <details className="master-derived">
                    <summary>SAP details (auto-filled — rarely edited)</summary>
                    <div className="grid">{extDerivedCols.map(renderField)}</div>
                  </details>
                )}
              </section>
            </div>
          </>
        ) : (
          <div className="master-field-groups single">
            <section>
              <div className="grid">{visibleCols.map(renderField)}</div>
              {state.tab === 'uoms' && (
                <div className="result" style={{ marginTop: 12 }}>
                  <b><i className="fa-solid fa-calculator" /> Preview</b>
                  <p className="hint" style={{ margin: '6px 0 0' }}>{uomPreview}</p>
                </div>
              )}
              {editing && <div className="hint" style={{ marginTop: 10 }}>{def.key}: {existing[def.key]} · CreatedAt: {existing.CreatedAt || '—'} · UpdatedAt: {existing.UpdatedAt || '—'}</div>}
            </section>
          </div>
        )}
        <div className="row" style={{ marginTop: 18 }}>
          <button className="btn primary" disabled={saving} onClick={save}>
            {saving ? 'Saving…' : hasDupes ? 'Confirm — Save as New Record' : 'Save'}
          </button>
          <button className="btn" onClick={onClose}>
            Cancel
          </button>
          {editing && !state.onSaved && (
            <button className="btn ghost master-edit-delete" disabled={saving} onClick={del}>
              <i className="fa-solid fa-trash-can" /> Delete
            </button>
          )}
        </div>
      </div>
    </Modal>
  );
}
