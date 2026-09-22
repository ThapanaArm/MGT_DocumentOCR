import { Fragment, useEffect, useState } from 'react';
import Modal, { ModalHeader } from '../Modal';
import { MASTER_DEF, M_LABEL } from '../../constants/fields';
import { createMaster, updateMaster, type MasterRow, type MastersData } from '../../api/masters';
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
  const fieldLabel = (c: (typeof def.cols)[number]) => {
    if (state.tab !== 'uoms' || !isMgt) return c.l;
    if (c.k === 'SapUom') return 'Zoho Unit';
    if (c.k === 'Factor') return 'Factor (1 document unit = ? Zoho units)';
    return c.l;
  };
  const editing = state.rowKey != null;
  const existing = editing
    ? { ...(masters[state.tab].find((x) => String(x[def.key]) === String(state.rowKey)) || {}), ...(state.prefill || {}) }
    : state.prefill || {};

  const setField = (k: string, v: string) => setForm((f) => ({ ...f, [k]: v }));

  // Whether a column is actually required in THIS context. Static `required` from MASTER_DEF, with
  // one company-specific relaxation: for a GLC (non-MGT) Ship-to, the SAP Ship-to code is optional
  // — some GLC delivery locations aren't mapped to / sent to SAP at all, so a Ship-to master can be
  // saved without one. MGT keeps it required.
  const isColRequired = (c: (typeof def.cols)[number]) => {
    if (!c.required) return false;
    if (state.tab === 'shiptos' && !isMgt && c.k === 'SapShipToCode') return false;
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
      showToast('Master data saved successfully');
    }
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
    <Modal open onClose={onClose}>
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
        <div className="master-field-groups">
          {(visibleCols.some((c) => c.source) ? ['document', 'external', 'system'] : ['all']).map((source) => (
          <section key={source} className={source === 'system' ? 'master-system-fields' : ''}>
          {source !== 'all' && <h3>{source === 'document' ? 'Customer and document data' : source === 'external' ? 'SAP / Zoho data' : 'Status and system data'}</h3>}
          <div className="grid">
          {visibleCols.filter((c) => source === 'all' || c.source === source).map((c) => (
            <Fragment key={c.k}>
            <div className="f">
              <label htmlFor={'master-' + c.k}>{fieldLabel(c)}{isColRequired(c) ? ' *' : ''}</label>
              {c.k === 'SalesOrg' ? (
                <select id={'master-' + c.k} value={form[c.k] ?? ''} onChange={(e) => { setField(c.k, e.target.value); if (state.tab === 'custmaterials' || state.tab === 'shiptos') setField('CustomerCode', ''); }}>
                  <option value="1000">1000 — MGT</option><option value="2000">2000 — GLC</option>
                </select>
              ) : c.source === 'system' ? (
                <select id={'master-' + c.k} value={Number(form[c.k] ?? 1)} onChange={(e) => setField(c.k, e.target.value)}><option value="1">1 — Active</option><option value="0">0 — Inactive</option></select>
              ) : c.ref ? (
                <select id={'master-' + c.k} value={form[c.k] ?? ''} onChange={(e) => setField(c.k, e.target.value)}>
                  {!c.blank && <option value="">— Select —</option>}
                  {c.blank && <option value="">— All materials (global rule) —</option>}
                  {(masters[c.ref] || []).filter((o) => (o.IsActive == null || !!Number(o.IsActive)) && (c.ref !== 'customers' || !form.SalesOrg || (state.tab !== 'custmaterials' && state.tab !== 'shiptos') || String(o.SalesOrg) === String(form.SalesOrg))).map((o, index) => {
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
                <input id={'master-' + c.k} value={form[c.k] ?? ''} onChange={(e) => setField(c.k, e.target.value)} />
              )}
              {c.source && <small className="master-field-help"><code>{c.k}</code>{c.help ? ' · ' + c.help : ''}</small>}
            </div>
            {/* Sits right beside the Material field per user request, rather than as a separate
                banner above the whole form — GLC material-confirm popup only (see DocumentPage's
                useSapMaterial / getSapLastPrice). Reference context only, never saved. */}
            {c.k === 'MaterialCode' && state.tab === 'uoms' && state.lastPrice !== undefined && (
              <div className="f">
                <label><i className="fa-solid fa-tag" /> Last Price</label>
                {state.lastPrice ? (
                  <input
                    type="text"
                    disabled
                    readOnly
                    value={`${state.lastPrice.pricePerUnit.toLocaleString()} / ${state.lastPrice.unit || 'unit'}${state.lastPrice.creationDate ? ` (billed ${state.lastPrice.creationDate})` : ''}`}
                  />
                ) : (
                  <input type="text" disabled readOnly value="No prior SAP billing found" />
                )}
                <small className="master-field-help">for reference only, confirm the unit with what Sales told CS to use</small>
              </div>
            )}
            </Fragment>
          ))}
          </div>
          {source === 'system' && editing && <div className="hint">{def.key}: {existing[def.key]} · CreatedAt: {existing.CreatedAt || '—'} · UpdatedAt: {existing.UpdatedAt || '—'}</div>}
          </section>
          ))}
        </div>
        <div className="row" style={{ marginTop: 18 }}>
          <button className="btn primary" disabled={saving} onClick={save}>
            {saving ? 'Saving…' : hasDupes ? 'Confirm — Save as New Record' : 'Save'}
          </button>
          <button className="btn" onClick={onClose}>
            Cancel
          </button>
        </div>
      </div>
    </Modal>
  );
}
