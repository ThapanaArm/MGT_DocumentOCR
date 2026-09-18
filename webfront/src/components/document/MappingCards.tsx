import { useEffect, useRef, useState } from 'react';
import { num } from '../../utils/format';
import type { DocHeader, DocModel, MapEntry, MapField, MapResult } from '../../api/documents';
import type { MastersData } from '../../api/masters';
import {
  searchSapBusinessPartner,
  findSapPartnerFunctions,
  type SapBusinessPartner,
  type SapPartnerFunctionLink,
  searchSapMaterials,
  getSapLastPrice,
  getSapCustomerPaymentTerms,
  getSapCustomerSalesAreas,
  type SapMaterial,
  type SapLastPrice,
  type SapCustomerPaymentTerms,
  type SapCustomerSalesArea,
  type SapSalesEmployee,
  type SapSalesEmployeeSuggestion,
} from '../../api/sap';
import {
  searchZohoAccount,
  getZohoAccountFull,
  getZohoAccountShipTosByCode,
  getZohoAccountShipTos,
  getZohoAccountSoldToByCode,
  getZohoAccountFullByCode,
  getZohoAccountSoldTo,
  getZohoDealsByAccountCode,
  type ZohoAccount,
  type ZohoShipToInfo,
  type ZohoDeal,
  type ZohoDealItem,
  type ZohoFullField,
} from '../../api/zoho';
import type { CompareField } from '../../api/compare';
import CompareModal, { type CompareCandidateItem } from './CompareModal';
import MaterialSearchSelect from './MaterialSearchSelect';
import { useMeta } from '../../state/MetaContext';

/* Ports mappingCards()/cmpCard()/sideList()/statusChip()/uomCell(). */

function SideList({ items, side }: { items?: MapField[]; side: 'doc' | 'sap' }) {
  if (!items || !items.length)
    return (
      <div className="hint" style={{ padding: '6px 0' }}>
        {side === 'sap' ? 'No data found in SAP yet' : '—'}
      </div>
    );
  return (
    <table className="cmp">
      <tbody>
        {items.map((f, i) => (
          <tr key={i}>
            <th>{f.label}</th>
            <td>
              {f.value || <span className="hint">—</span>}
              {f.match === true && (
                <span className="badge b-ok" style={{ padding: '1px 7px', marginLeft: 4 }}>
                  <i className="fa-solid fa-check" />
                </span>
              )}
              {f.match === false && (
                <span className="badge b-warn" style={{ padding: '1px 7px', marginLeft: 4 }}>
                  <i className="fa-solid fa-not-equal" />
                </span>
              )}
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

function StatusChip({ st }: { st: string }) {
  if (st === 'ok') return <span className="badge b-ok"><i className="fa-solid fa-check" /> Auto-matched</span>;
  // GLC ship-to optional: the person explicitly chose a "no ship-to" fallback (use sold-to / omit) —
  // a neutral chip, never the green "Auto-matched", so "matched" only ever shows a real match.
  if (st === 'skip') return <span className="badge b-idle"><i className="fa-solid fa-check" /> ยืนยันแล้ว</span>;
  // GLC ship-to optional and not yet decided: the person must pick a real ship-to or a fallback
  // before sending. A warn chip (blocking) rather than a scary red "Not found".
  if (st === 'needchoice') return <span className="badge b-warn"><i className="fa-solid fa-hand-pointer" /> ต้องเลือก Ship-to</span>;
  if (st === 'manual') return <span className="badge b-warn"><i className="fa-solid fa-pen" /> Manually selected</span>;
  if (st === 'convert') return <span className="badge b-ok"><i className="fa-solid fa-right-left" /> Unit converted</span>;
  if (st === 'fail') return <span className="badge b-fail"><i className="fa-solid fa-xmark" /> Not found</span>;
  if (st === 'unitfail') return <span className="badge b-fail"><i className="fa-solid fa-xmark" /> No unit conversion rule</span>;
  return <span className="badge b-idle">Pending Mapping</span>;
}

function CmpCard({
  no,
  title,
  r,
  picker,
  sapSlot,
  sourceLabel,
  children,
}: {
  no: string | number;
  title: string;
  r: MapEntry;
  picker?: React.ReactNode;
  /** Replaces the default "Data from SAP" list — used by the Customer card to show a live SAP
   *  (or, for an MGT-opened Sales Order, Zoho CRM) search instead of the static
   *  "No data found in SAP yet" placeholder. */
  sapSlot?: React.ReactNode;
  /** Label for the right-hand column when it isn't SAP — e.g. "Data from Zoho CRM" for an
   *  MGT-opened Sales Order's Customer card. Defaults to "Data from SAP". */
  sourceLabel?: string;
  children?: React.ReactNode;
}) {
  const st = r.status || 'idle';
  return (
    <div className={'cmp-card ' + (st === 'fail' ? 'bad' : '')}>
      <div className="cmp-head">
        <span className="cmp-no">{no}</span>
        <b>{title}</b>
        <StatusChip st={st} />
        {r.sapCode && (
          <span className="badge b-ok" title="Code posted to SAP">
            SAP: {r.sapCode}
          </span>
        )}
        <span className="hint">{r.method || ''}</span>
        <div className="sp" />
        {picker}
      </div>
      <div className="cmp-body">
        <div className="cmp-col">
          <div className="cmp-label"><i className="fa-solid fa-file-lines" /> Data from Document</div>
          <SideList items={r.doc} side="doc" />
        </div>
        <div className="cmp-arrow">→</div>
        <div className="cmp-col sap">
          <div className="cmp-label"><i className="fa-solid fa-building-columns" /> {sourceLabel ?? 'Data from SAP'}</div>
          {sapSlot ?? <SideList items={r.sap} side="sap" />}
        </div>
      </div>
      {children}
    </div>
  );
}

/** Handed up to DocumentPage when a live Customer search finds more than one candidate — instead
 *  of a separate popup, the ambiguity gets resolved by chatting about it right in the document's
 *  existing "Chat to Fix Data" box (see DocumentPage's pendingMatch/matchChatLog). `onSelect`
 *  already knows which underlying onUse handler (SAP or Zoho) to call, so the caller (whoever
 *  renders the chat) doesn't need to know which system this candidate came from. */
export interface CustomerMatchCandidate {
  id: string;
  label: string;
  fields: CompareField[];
}
export interface CustomerMatchProposal {
  docFields: CompareField[];
  candidates: CustomerMatchCandidate[];
  /** Loads the full record for one candidate on demand (Zoho only, for now) — DocumentPage calls
   *  this for every candidate as soon as it proposes the match, so richer data is ready by the
   *  time the person actually replies. */
  fetchFull?: (id: string) => Promise<CompareField[]>;
  onSelect: (id: string) => void;
}

/* Live SAP search shown directly in the Customer card's "Data from SAP" column when local
   matching fails — this is the point of entry users actually look at first, so results land
   here instead of being hidden inside the "Add New Customer" modal. One click ("Use & Save")
   creates the local Customer master row from the SAP record and applies it as the match; when
   there's more than one candidate, onProposeMatch also raises it as a question in the chat box
   below so the person can resolve it conversationally instead. */
function SapCustomerPanel({
  customerName,
  taxId,
  address,
  companyCode,
  disabled,
  onUse,
  onProposeMatch,
  onClearMatch,
}: {
  customerName?: string;
  taxId?: string;
  /** The document's own Sold-to/billing address (doc.header.customerAddress) — the address
   *  printed next to the customer's own name, distinct from any separate Ship-to delivery
   *  address. Included as a disambiguation hint when two candidates otherwise look identical
   *  (e.g. same name/tax ID, different branch). */
  address?: string;
  companyCode: string;
  disabled?: boolean;
  onUse: (bp: SapBusinessPartner) => void;
  onProposeMatch?: (proposal: CustomerMatchProposal) => void;
  onClearMatch?: () => void;
}) {
  const [results, setResults] = useState<SapBusinessPartner[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [searched, setSearched] = useState(false);

  const docName = (customerName || '').trim();
  const tax = (taxId || '').trim();

  // The first lookup uses the name read from the document. If it is not useful, the person can
  // enter a different term and explicitly search again.
  const [manualQuery, setManualQuery] = useState('');
  const [appliedQuery, setAppliedQuery] = useState<string | null>(null);
  useEffect(() => { setManualQuery(''); setAppliedQuery(null); }, [docName]);
  const name = appliedQuery ?? docName;

  useEffect(() => {
    if (!name && !tax) {
      setResults([]);
      setSearched(false);
      onClearMatch?.();
      return;
    }
    let cancelled = false;
    setLoading(true);
    setError(null);
    searchSapBusinessPartner({ name, taxId: appliedQuery ? undefined : tax })
      .then((r) => {
        if (cancelled) return;
        // Defensive: an unexpected response shape (e.g. an HTML page from a stale/mismatched
        // backend build instead of real JSON) must not crash this panel — surface it as a
        // search error instead of calling setResults(undefined) and blowing up on the next
        // .length check.
        if (!Array.isArray(r?.results)) {
          setResults([]);
          setError('Unexpected response from the server (is the backend up to date?)');
          return;
        }
        setResults(r.results);
        if (r.results.length > 1) {
          onProposeMatch?.({
            docFields: [
              { label: 'Customer Name', value: name },
              { label: 'Tax ID', value: tax },
              { label: 'Customer Address (from document)', value: address },
            ],
            candidates: r.results.map((bp) => ({
              id: bp.businessPartnerId,
              label: bp.businessPartnerId,
              fields: [
                { label: 'Name', value: bp.businessPartnerFullName || bp.businessPartnerName },
                { label: 'Tax ID', value: bp.taxId },
                { label: 'Blocked', value: bp.businessPartnerIsBlocked ? 'Yes' : 'No' },
                { label: 'City', value: bp.addressCity },
                { label: 'Street', value: bp.addressStreet },
              ],
            })),
            onSelect: (id) => {
              const bp = r.results.find((x) => x.businessPartnerId === id);
              if (bp) onUse(bp);
            },
          });
        } else {
          onClearMatch?.();
        }
      })
      .catch((e) => {
        if (!cancelled) {
          setResults([]);
          setError(e?.message || 'SAP search failed');
        }
      })
      .finally(() => {
        if (!cancelled) {
          setLoading(false);
          setSearched(true);
        }
      });
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [name, tax, companyCode]);

  const hint = (text: string) => <div className="hint" style={{ padding: '6px 0' }}>{text}</div>;
  const { ocrProviders } = useMeta();
  const [aiOpen, setAiOpen] = useState(false);

  const runManualSearch = () => {
    const query = manualQuery.trim();
    if (query) setAppliedQuery(query);
  };
  const renderManualSearchBox = () => (
    <div style={{ marginTop: 8, display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
      <input type="search" className="txt" style={{ minWidth: 240 }} value={manualQuery}
        onChange={(e) => setManualQuery(e.target.value)}
        onKeyDown={(e) => { if (e.key === 'Enter') runManualSearch(); }}
        placeholder="ใส่ชื่อลูกค้าหรือคำค้นหา" aria-label="คำค้นหาลูกค้า" />
      <button className="btn sm" onClick={runManualSearch} disabled={!manualQuery.trim() || loading}>
        <i className="fa-solid fa-magnifying-glass" /> ค้นหา
      </button>
    </div>
  );

  if (!name && !tax)
    return <div className="hint" style={{ padding: '6px 0' }}><div>กรอกคำค้นหาเพื่อค้นหาลูกค้าใน SAP</div>{renderManualSearchBox()}</div>;
  if (loading) return hint('Searching SAP…');
  if (error)
    return <div className="hint" style={{ padding: '6px 0' }}><div>ค้นหา SAP ไม่สำเร็จ: {error}</div>{renderManualSearchBox()}</div>;
  if (searched && results.length === 0)
    return (
      <div className="hint" style={{ padding: '6px 0' }}>
        <div>No match found in SAP — use "+ Add New Customer" to enter it manually</div>
        {renderManualSearchBox()}
      </div>
    );

  const sorted = [...results].sort(
    (a, b) => Number(!!a.businessPartnerIsBlocked) - Number(!!b.businessPartnerIsBlocked),
  );
  const aiCandidates: CompareCandidateItem<SapBusinessPartner>[] = results.map((bp) => ({
    id: bp.businessPartnerId,
    label: bp.businessPartnerId,
    fields: [
      { label: 'Name', value: bp.businessPartnerFullName || bp.businessPartnerName },
      { label: 'Tax ID', value: bp.taxId },
      { label: 'Blocked', value: bp.businessPartnerIsBlocked ? 'Yes' : 'No' },
      { label: 'City', value: bp.addressCity },
      { label: 'Street', value: bp.addressStreet },
    ],
    raw: bp,
  }));

  return (
    <>
      {results.length > 1 && (
        <div className="hint" style={{ padding: '6px 0', display: 'flex', alignItems: 'center', gap: 8 }}>
          <span>Found {results.length} matches in SAP — ask AI, ask in the chat box below, or pick one directly:</span>
          <button className="btn sm" onClick={() => setAiOpen(true)}>
            <i className="fa-solid fa-wand-magic-sparkles" /> Ask AI to match
          </button>
        </div>
      )}
      <table className="cmp">
        <tbody>
          {sorted.map((bp) => (
            <tr key={bp.businessPartnerId}>
              <td>
                <b>{bp.businessPartnerId}</b> — {bp.businessPartnerFullName || bp.businessPartnerName}
                {bp.businessPartnerIsBlocked && (
                  <span className="badge b-fail" style={{ marginLeft: 4 }}>Blocked</span>
                )}
                {/* Always shown when SAP has one on file — even if the document itself had no Tax ID
                    to search with, this is what SAP says the customer's Tax ID actually is. */}
                {bp.taxId && <div className="hint">Tax ID: {bp.taxId}</div>}
                {(bp.addressCity || bp.addressStreet) && (
                  <div className="hint">{[bp.addressStreet, bp.addressCity].filter(Boolean).join(', ')}</div>
                )}
              </td>
              <td>
                <button className="btn sm primary" disabled={disabled} onClick={() => onUse(bp)}>
                  Use &amp; Save
                </button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      <div className="hint" style={{ padding: '6px 0' }}>
        <div>ไม่ใช่รายที่ต้องการ? ใส่คำค้นหาอื่นได้เลย</div>
        {renderManualSearchBox()}
      </div>
      <CompareModal<SapBusinessPartner>
        open={aiOpen}
        onClose={() => setAiOpen(false)}
        title="AI Suggested Match — Customer (SAP)"
        docFields={[
          { label: 'Customer Name', value: name },
          { label: 'Tax ID', value: tax },
          { label: 'Customer Address (from document)', value: address },
        ]}
        candidates={aiCandidates}
        providers={ocrProviders || []}
        onUse={(bp) => {
          onUse(bp);
          setAiOpen(false);
        }}
      />
    </>
  );
}

/* How well a SAP material description matches the document's line description — used to sort the
   SAP candidates "closest first" (per user), the same idea as the Customer panel showing the best
   match at the top. Token overlap (order-independent) plus a strong bonus when the document text
   appears as a whole substring. Works on Latin and Thai text; returns 0 when either side is empty
   (e.g. a Thai document line against English SAP descriptions), which just leaves SAP's own order
   untouched. */
function materialMatchScore(query: string, description: string): number {
  const norm = (s: string) => (s || '').toLowerCase().replace(/[^a-z0-9฀-๿]+/gi, ' ').trim();
  const q = norm(query);
  const d = norm(description);
  if (!q || !d) return 0;
  const qTokens = q.split(/\s+/).filter(Boolean);
  const dTokens = new Set(d.split(/\s+/).filter(Boolean));
  let overlap = 0;
  for (const t of qTokens) if (dTokens.has(t)) overlap += 1;
  let score = overlap / Math.max(qTokens.length, 1);
  if (d.includes(q)) score += 1; // whole document text found inside the description — strongest signal
  return score;
}

/* A first keyword to seed the SAP search from the document's line description. SAP substringof is
   an exact (upper-cased) substring test, so the whole multi-word line rarely matches — the most
   "code-like" token (one containing digits, e.g. a grade/part number) or otherwise the longest
   token is the best single guess. Empty when nothing usable is found, in which case the panel just
   waits for the person to type a keyword. */
function suggestedMaterialKeyword(desc: string): string {
  const tokens = (desc || '').split(/[^A-Za-z0-9฀-๿]+/).filter((t) => t.length >= 3);
  if (tokens.length === 0) return '';
  const codeish = tokens.find((t) => /\d/.test(t));
  return (codeish || [...tokens].sort((a, b) => b.length - a.length)[0] || '').trim();
}

/* SAP material search shown in the Material card's "Data from SAP" column, mirroring
   SapCustomerPanel: it lists the closest-matching SAP materials (sorted most-similar first) with a
   one-click "Use & Save", plus a manual search box to refine the keyword. Picking one hands the
   SAP material up to onUse, which creates the CustomerMaterial mapping row and applies it to the
   line (see DocumentPage.useSapMaterial). Replaces the plain <select>, which couldn't show or
   compare the candidates. */
function SapMaterialPanel({
  docDescription,
  docCode,
  plant,
  customer,
  disabled,
  onUse,
}: {
  docDescription?: string;
  /** The customer's own material code on the document line (line.extCode), shown to the AI as an
   *  extra matching signal alongside the description. */
  docCode?: string;
  plant: string;
  /** The matched customer's SAP code (SoldToParty). When present, each search result shows that
   *  customer's Last Price for that material (fetched live) so the price is visible BEFORE picking
   *  — GLC only; blank/absent = the Last Price column is simply omitted. */
  customer?: string;
  disabled?: boolean;
  onUse: (m: SapMaterial) => void;
}) {
  const [results, setResults] = useState<SapMaterial[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [searched, setSearched] = useState(false);
  const { ocrProviders } = useMeta();
  const [aiOpen, setAiOpen] = useState(false);
  // Last Price per result material for THIS customer. undefined = not fetched yet, null = looked up
  // but SAP had no prior billing, object = found. Best-effort: a failed lookup just shows nothing.
  const [lastPrices, setLastPrices] = useState<Record<string, SapLastPrice | null>>({});

  // Fetch each candidate's Last Price once the SAP results (and the customer) are known, so the
  // price shows in the list before the person picks a row. One call per result material — usually
  // only a handful — run in parallel; each resolves independently and best-effort.
  useEffect(() => {
    const cust = (customer || '').trim();
    if (!cust || results.length === 0) { setLastPrices({}); return; }
    let cancelled = false;
    setLastPrices({});
    Promise.all(results.map(async (m) => {
      try {
        const r = await getSapLastPrice(cust, m.materialCode);
        return [m.materialCode, r.price] as const;
      } catch {
        return [m.materialCode, null] as const;
      }
    })).then((pairs) => {
      if (cancelled) return;
      const next: Record<string, SapLastPrice | null> = {};
      for (const [mc, p] of pairs) next[mc] = p;
      setLastPrices(next);
    });
    return () => { cancelled = true; };
  }, [results, customer]);

  const renderLastPrice = (materialCode: string) => {
    if (!customer) return null;
    const lp = lastPrices[materialCode];
    return (
      <div className="hint" style={{ marginTop: 3 }}>
        <i className="fa-solid fa-tag" />{' '}
        {lp === undefined
          ? 'Last Price: กำลังโหลด…'
          : lp
            ? `Last Price: ${lp.pricePerUnit.toLocaleString()} / ${lp.unit || 'unit'}${lp.creationDate ? ` (${lp.creationDate})` : ''}`
            : 'Last Price: ไม่มีประวัติการขายกับลูกค้ารายนี้'}
      </div>
    );
  };

  const docDesc = (docDescription || '').trim();
  const seed = suggestedMaterialKeyword(docDesc);

  // First lookup uses the keyword guessed from the document line; the person can then type a
  // different term and search again.
  const [manualQuery, setManualQuery] = useState('');
  const [appliedQuery, setAppliedQuery] = useState<string | null>(null);
  useEffect(() => { setManualQuery(''); setAppliedQuery(null); }, [docDesc]);
  const query = appliedQuery ?? seed;

  useEffect(() => {
    if (!query || query.length < 2) {
      setResults([]);
      setSearched(false);
      return;
    }
    let cancelled = false;
    setLoading(true);
    setError(null);
    searchSapMaterials(query, plant)
      .then((r) => {
        if (cancelled) return;
        if (!Array.isArray(r?.results)) {
          setResults([]);
          setError('Unexpected response from the server (is the backend up to date?)');
          return;
        }
        setResults(r.results);
      })
      .catch((e) => {
        if (!cancelled) {
          setResults([]);
          setError(e?.message || 'SAP search failed');
        }
      })
      .finally(() => {
        if (!cancelled) {
          setLoading(false);
          setSearched(true);
        }
      });
    return () => { cancelled = true; };
  }, [query, plant]);

  const runManualSearch = () => {
    const q = manualQuery.trim();
    if (q) setAppliedQuery(q);
  };
  const renderSearchBox = () => (
    <div style={{ marginTop: 8, display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
      <input type="search" className="txt" style={{ minWidth: 220 }} value={manualQuery}
        onChange={(e) => setManualQuery(e.target.value)}
        onKeyDown={(e) => { if (e.key === 'Enter') runManualSearch(); }}
        placeholder="ค้นหา Material Description…" aria-label="ค้นหา Material ด้วย Description" />
      <button className="btn sm" onClick={runManualSearch} disabled={!manualQuery.trim() || loading}>
        <i className="fa-solid fa-magnifying-glass" /> ค้นหา
      </button>
    </div>
  );

  const hint = (text: string) => <div className="hint" style={{ padding: '6px 0' }}>{text}</div>;

  if (!query || query.length < 2)
    return <div className="hint" style={{ padding: '6px 0' }}><div>พิมพ์คำค้นหาเพื่อค้นหา Material ใน SAP</div>{renderSearchBox()}</div>;
  if (loading) return hint('กำลังค้นหา SAP…');
  if (error)
    return <div className="hint" style={{ padding: '6px 0' }}><div>ค้นหา SAP ไม่สำเร็จ: {error}</div>{renderSearchBox()}</div>;
  if (searched && results.length === 0)
    return (
      <div className="hint" style={{ padding: '6px 0' }}>
        <div>ไม่พบใน SAP — ลองคำค้นหาอื่น หรือใช้ "+ Add New Material"</div>
        {renderSearchBox()}
      </div>
    );

  // Closest first: rank every candidate against the document's line description.
  const sorted = [...results]
    .map((m) => ({ m, score: materialMatchScore(docDesc, m.materialDescription) }))
    .sort((a, b) => b.score - a.score);

  const aiCandidates: CompareCandidateItem<SapMaterial>[] = sorted.map(({ m }) => ({
    id: m.materialCode,
    label: m.materialCode,
    fields: [
      { label: 'Material Code', value: m.materialCode },
      { label: 'Description', value: m.materialDescription },
    ],
    raw: m,
  }));

  return (
    <>
      <div className="hint" style={{ padding: '6px 0', display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
        <span>พบ {results.length} รายการใน SAP — เรียงจากใกล้เคียงที่สุด เลือกได้เลย:</span>
        {results.length > 1 && (
          <button className="btn sm" onClick={() => setAiOpen(true)}>
            <i className="fa-solid fa-wand-magic-sparkles" /> Ask AI to match
          </button>
        )}
      </div>
      <table className="cmp">
        <tbody>
          {sorted.map(({ m }) => (
            <tr key={m.materialCode}>
              <td>
                <b>{m.materialCode}</b> — {m.materialDescription}
                {renderLastPrice(m.materialCode)}
              </td>
              <td>
                <button className="btn sm primary" disabled={disabled} onClick={() => onUse(m)}>
                  Use &amp; Save
                </button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      <div className="hint" style={{ padding: '6px 0' }}>
        <div>ไม่ใช่รายที่ต้องการ? ใส่คำค้นหาอื่นได้เลย</div>
        {renderSearchBox()}
      </div>
      <CompareModal<SapMaterial>
        open={aiOpen}
        onClose={() => setAiOpen(false)}
        title="AI Suggested Match — Material (SAP)"
        docFields={[
          { label: 'Material (from document)', value: docDesc },
          ...(docCode ? [{ label: 'Customer Material Code', value: docCode }] : []),
        ]}
        candidates={aiCandidates}
        providers={ocrProviders || []}
        onUse={(m) => {
          onUse(m);
          setAiOpen(false);
        }}
      />
    </>
  );
}

/* MGT/Zoho counterpart to SapMaterialPanel above. There is no global Zoho material search (per
   ZohoSalesOrderClient), so the candidates are the matched Deal's own Ordered Items — the same
   products the Zoho Sales Order will be built from. Ranked closest-first against the document line,
   picking one saves a CustomerMaterial mapping (customer code/desc -> that item's Material Code) so
   the line — and future documents from this customer — resolve to it. */
function ZohoMaterialPanel({
  dealItems,
  docDescription,
  docCode,
  disabled,
  onUse,
}: {
  dealItems: ZohoDealItem[];
  docDescription?: string;
  docCode?: string;
  disabled?: boolean;
  onUse: (item: ZohoDealItem) => void;
}) {
  const [query, setQuery] = useState('');
  const hint = (text: string) => <div className="hint" style={{ padding: '6px 0' }}>{text}</div>;
  if (!dealItems || dealItems.length === 0)
    return hint('เลือก Deal ก่อน เพื่อดึงรายการสินค้า (Ordered Items) จาก Zoho');

  const docDesc = (docDescription || '').trim();
  const code = (docCode || '').trim().toLowerCase();
  const sorted = [...dealItems]
    .map((it) => {
      const codeHit = code.length > 0 && (it.materialCode || '').trim().toLowerCase() === code;
      const score = (codeHit ? 1000 : 0) +
        materialMatchScore(docDesc, it.materialName || it.materialDescription || '');
      return { it, score };
    })
    .sort((a, b) => b.score - a.score);
  const normalizedQuery = query.trim().toLocaleLowerCase();
  const visible = normalizedQuery
    ? sorted.filter(({ it }) =>
        [it.materialCode, it.materialName, it.materialDescription, it.materialGroup]
          .some((value) => (value || '').toLocaleLowerCase().includes(normalizedQuery)),
      )
    : sorted;

  return (
    <>
      <div className="hint" style={{ padding: '6px 0' }}>
        รายการสินค้าจาก Deal นี้ ({dealItems.length}) — เรียงจากใกล้เคียงที่สุด เลือกเพื่อบันทึกเป็น CustomerMaterial:
      </div>
      <table className="cmp">
        <tbody>
          {visible.map(({ it }, idx) => {
            const label = it.materialName || it.materialDescription || '';
            return (
              <tr key={(it.materialCode || it.materialId || 'row') + ':' + idx}>
                <td>
                  <b>{it.materialCode || '(ไม่มีรหัส)'}</b>{label ? ' — ' + label : ''}
                  {it.materialGroup && <div className="hint">{it.materialGroup}</div>}
                </td>
                <td>
                  <button
                    className="btn sm primary"
                    disabled={disabled || !(it.materialCode || '').trim()}
                    onClick={() => onUse(it)}
                  >
                    Use &amp; Save
                  </button>
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
      <div className="hint" style={{ padding: '6px 0' }}>
        <div>ไม่ใช่รายการที่ต้องการ? พิมพ์ชื่อหรือรหัส Material เพื่อกรองผลลัพธ์ได้</div>
        <div style={{ marginTop: 8, display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
          <input
            type="search"
            className="txt"
            style={{ minWidth: 220 }}
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder="ชื่อหรือรหัส Material…"
            aria-label="ค้นหา Material ใน Deal"
          />
          {normalizedQuery && <span>พบ {visible.length} รายการ</span>}
        </div>
      </div>
    </>
  );
}

/* MGT-side counterpart to SapCustomerPanel above — live search against Zoho CRM's Accounts
   module instead of SAP Business Partner. Field names (Account_Name, Tax_ID, Branch_Name,
   Account_Code) are confirmed against Megachem's Zoho CRM user manual (AO-CRM-UM-2026-003,
   section 5.2.8), not guessed. Branch_Name is shown as a real disambiguator between branches
   sharing one Tax ID — unlike the SAP side, where the equivalent (TH3) turned out unreliable and
   was dropped. */
function ZohoCustomerPanel({
  customerName,
  taxId,
  address,
  disabled,
  onUse,
  onProposeMatch,
  onClearMatch,
}: {
  customerName?: string;
  taxId?: string;
  /** Same rationale as SapCustomerPanel's `address` — the document's own Sold-to/billing
   *  address, used only as an extra disambiguation hint. */
  address?: string;
  disabled?: boolean;
  onUse: (acc: ZohoAccount) => void;
  onProposeMatch?: (proposal: CustomerMatchProposal) => void;
  onClearMatch?: () => void;
}) {
  const [results, setResults] = useState<ZohoAccount[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [searched, setSearched] = useState(false);
  // Sold-to address per candidate, fetched lazily once results come in -- undefined = still
  // loading, null = looked up but nothing found. Shown next to each row so the person can
  // eyeball which candidate is right without opening the full chat compare.
  const [addresses, setAddresses] = useState<Record<string, ZohoShipToInfo | null>>({});

  const docName = (customerName || '').trim();
  const tax = (taxId || '').trim();

  const [manualQuery, setManualQuery] = useState('');
  const [appliedQuery, setAppliedQuery] = useState<string | null>(null);
  useEffect(() => { setManualQuery(''); setAppliedQuery(null); }, [docName]);
  const name = appliedQuery ?? docName;

  useEffect(() => {
    if (!name && !tax) {
      setResults([]);
      setAddresses({});
      setSearched(false);
      onClearMatch?.();
      return;
    }
    let cancelled = false;
    setLoading(true);
    setError(null);
    setAddresses({});
    searchZohoAccount({ name, taxId: appliedQuery ? undefined : tax })
      .then((r) => {
        if (cancelled) return;
        // Same defensive check as SapCustomerPanel above — never trust the response shape blindly.
        if (!Array.isArray(r?.results)) {
          setResults([]);
          setError('Unexpected response from the server (is the backend up to date?)');
          return;
        }
        setResults(r.results);
        r.results.forEach((acc) => {
          getZohoAccountSoldTo(acc.accountId)
            .then((info) => {
              if (!cancelled) setAddresses((m) => ({ ...m, [acc.accountId]: info ?? null }));
            })
            .catch(() => {
              if (!cancelled) setAddresses((m) => ({ ...m, [acc.accountId]: null }));
            });
        });
        if (r.results.length > 1) {
          onProposeMatch?.({
            docFields: [
              { label: 'Customer Name', value: name },
              { label: 'Tax ID', value: tax },
              { label: 'Customer Address (from document)', value: address },
            ],
            candidates: r.results.map((acc) => ({
              id: acc.accountId,
              label: acc.accountCode || acc.accountId,
              fields: [
                { label: 'Name', value: acc.accountName },
                { label: 'Tax ID', value: acc.taxId },
                { label: 'Branch', value: acc.branchName },
                { label: 'Account Type', value: acc.accountType },
              ],
            })),
            // Loaded once per candidate as soon as the match is proposed, so the whole Zoho
            // Account record (not just these 4 curated fields) is ready by the time the person
            // actually replies in chat — same idea CompareModal used to do on open.
            fetchFull: (id) => getZohoAccountFull(id).then((full) => full.fields),
            onSelect: (id) => {
              const acc = r.results.find((x) => x.accountId === id);
              if (acc) onUse(acc);
            },
          });
        } else {
          onClearMatch?.();
        }
      })
      .catch((e) => {
        if (!cancelled) {
          setResults([]);
          setError(e?.message || 'Zoho CRM search failed');
        }
      })
      .finally(() => {
        if (!cancelled) {
          setLoading(false);
          setSearched(true);
        }
      });
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [name, tax, appliedQuery]);

  const runManualSearch = () => {
    const query = manualQuery.trim();
    if (query) setAppliedQuery(query);
  };
  const renderManualSearchBox = () => (
    <div style={{ marginTop: 8, display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
      <input
        type="search"
        className="txt"
        style={{ minWidth: 240 }}
        value={manualQuery}
        onChange={(e) => setManualQuery(e.target.value)}
        onKeyDown={(e) => { if (e.key === 'Enter') runManualSearch(); }}
        placeholder="ใส่ชื่อลูกค้าหรือคำค้นหา"
        aria-label="คำค้นหาลูกค้า"
      />
      <button className="btn sm" onClick={runManualSearch} disabled={!manualQuery.trim() || loading}>
        <i className="fa-solid fa-magnifying-glass" /> ค้นหา
      </button>
    </div>
  );

  const hint = (text: string) => <div className="hint" style={{ padding: '6px 0' }}>{text}</div>;
  const { ocrProviders } = useMeta();
  const [aiOpen, setAiOpen] = useState(false);
  const aiCandidates: CompareCandidateItem<ZohoAccount>[] = results.map((acc) => ({
    id: acc.accountId,
    label: acc.accountCode || acc.accountId,
    fields: [
      { label: 'Name', value: acc.accountName },
      { label: 'Tax ID', value: acc.taxId },
      { label: 'Branch', value: acc.branchName },
      { label: 'Account Type', value: acc.accountType },
    ],
    raw: acc,
  }));

  if (!name && !tax)
    return <div className="hint" style={{ padding: '6px 0' }}><div>กรอกคำค้นหาเพื่อค้นหาลูกค้าใน Zoho CRM</div>{renderManualSearchBox()}</div>;
  if (loading) return hint('Searching Zoho CRM…');
  if (error)
    return <div className="hint" style={{ padding: '6px 0' }}><div>ค้นหา Zoho CRM ไม่สำเร็จ: {error}</div>{renderManualSearchBox()}</div>;
  if (searched && results.length === 0)
    return <div className="hint" style={{ padding: '6px 0' }}><div>No match found in Zoho CRM — use "+ Add New Customer" to enter it manually</div>{renderManualSearchBox()}</div>;

  return (
    <>
      {results.length > 1 && (
        <div className="hint" style={{ padding: '6px 0', display: 'flex', alignItems: 'center', gap: 8 }}>
          <span>Found {results.length} matches in Zoho CRM — ask AI, ask in the chat box below, or pick one directly:</span>
          <button className="btn sm" onClick={() => setAiOpen(true)}>
            <i className="fa-solid fa-wand-magic-sparkles" /> Ask AI to match
          </button>
        </div>
      )}
      <table className="cmp">
        <tbody>
          {results.map((acc) => (
            <tr key={acc.accountId}>
              <td>
                <b>{acc.accountCode || acc.accountId}</b> — {acc.accountName}
                {acc.branchName && acc.branchName.trim() !== '-' && (
                  <span className="badge b-idle" style={{ marginLeft: 4 }}>{acc.branchName}</span>
                )}
                {acc.taxId && <div className="hint">Tax ID: {acc.taxId}</div>}
                {acc.accountType && <div className="hint">Type: {acc.accountType}</div>}
                {addresses[acc.accountId] === undefined ? (
                  <div className="hint">Loading address…</div>
                ) : addresses[acc.accountId]?.address ? (
                  <div className="hint">Address: {addresses[acc.accountId]!.address}</div>
                ) : (
                  <div className="hint">(no address on file)</div>
                )}
              </td>
              <td>
                <button className="btn sm primary" disabled={disabled} onClick={() => onUse(acc)}>
                  Use &amp; Save
                </button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      <div className="hint" style={{ padding: '6px 0' }}>
        <div>ไม่ใช่รายที่ต้องการ? ใส่คำค้นหาอื่นได้เลย</div>
        {renderManualSearchBox()}
      </div>
      <CompareModal<ZohoAccount>
        open={aiOpen}
        onClose={() => setAiOpen(false)}
        title="AI Suggested Match — Customer (Zoho CRM)"
        docFields={[
          { label: 'Customer Name', value: name },
          { label: 'Tax ID', value: tax },
          { label: 'Customer Address (from document)', value: address },
        ]}
        candidates={aiCandidates}
        providers={ocrProviders || []}
        fetchFullFields={(c) => getZohoAccountFull(c.id).then((full) => full.fields)}
        onUse={(acc) => {
          onUse(acc);
          setAiOpen(false);
        }}
      />
    </>
  );
}

/* Every confirmed raw sub-field of one Sold-to/Ship-to address, shown as-is rather than only a
   collapsed joined string -- per request, pull everything Zoho has on file and just display it.
   Field names/order match the manual's own field tables (section 5.2.8 / 5.4.3). */
function AddressFieldRows({ info }: { info: ZohoShipToInfo }) {
  const allRows: [string, string | null | undefined][] = [
    ['Code', info.code],
    ['House Number', info.houseNumber],
    ['Street', info.street],
    ['Street 2', info.street2],
    ['Street 3', info.street3],
    ['Street 4', info.street4],
    ['Street 5', info.street5],
    ['District', info.district],
    ['City', info.city],
    ['Difference City', info.differenceCity],
    ['Post Code', info.postCode],
    ['Country / Reg', info.countryReg],
  ];
  const rows = allRows.filter(([, v]) => !!v && v.trim() !== '');
  if (rows.length === 0) return <span className="hint">(no address fields on file)</span>;
  return (
    <table className="cmp">
      <tbody>
        {rows.map(([label, value]) => (
          <tr key={label}>
            <td className="hint">{label}</td>
            <td>{value}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

/* GLC/SAP counterpart to SapCustomerPanel above (see also ZohoShipToPanel just below, the MGT
   equivalent): live SAP search shown in the Ship-to sub-section's "Data from SAP" column when
   the local shiptos master has no match for this Customer. Per user: since the Customer is
   already matched to a real SAP Business Partner (its own code is right there on the Customer
   card), Ship-to shouldn't need a fresh name search as its PRIMARY method -- SAP already knows
   which Ship-tos are linked to that Sold-to via A_CustSalesPartnerFunc (PartnerFunction "SH"), so
   this looks those up directly by the matched Sold-to's SAP code first.
   Fallback added 2026-09-10, per Megachem: a Ship-to can exist in SAP as its own Business Partner
   without yet being linked to this particular Sold-to via a partner function (a data-entry gap on
   SAP's side, not a search-phrasing problem) -- this offers a manual name search for that case,
   reusing the exact same /api/sap/business-partner?name= search (Ship-tos are Business Partners
   too) rather than inventing a separate Ship-to-specific search.
   Now shown UNCONDITIONALLY (2026-09-15 fix), not only when the primary lookup is empty: the
   auto-linked partner-function result can itself be stale or simply wrong, and the person needs a
   way to search fresh and override it -- not just when nothing was linked at all. */
function SapShipToPanel({
  soldToSapCode,
  docShipToName,
  salesOrganization,
  disabled,
  onUse,
}: {
  /** The already-matched Sold-to's own SAP Business Partner code (the Customer card's
   *  MapEntry.sapCode) -- required, since the primary lookup is keyed off this, not off any
   *  document text. */
  soldToSapCode?: string;
  /** The document's own Ship-to name, if read off it -- prefills the fallback name-search box
   *  below so the person doesn't have to retype what the document already says. */
  docShipToName?: string;
  salesOrganization: string;
  disabled?: boolean;
  onUse: (link: SapPartnerFunctionLink) => void;
}) {
  const [results, setResults] = useState<SapPartnerFunctionLink[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [searched, setSearched] = useState(false);

  useEffect(() => {
    if (!soldToSapCode) {
      setResults([]);
      setSearched(false);
      return;
    }
    let cancelled = false;
    setLoading(true);
    setError(null);
    findSapPartnerFunctions(soldToSapCode, salesOrganization, 'SH')
      .then((r) => {
        if (cancelled) return;
        if (!Array.isArray(r?.results)) {
          setResults([]);
          setError('Unexpected response from the server (is the backend up to date?)');
          return;
        }
        setResults(r.results);
      })
      .catch((e) => {
        if (!cancelled) {
          setResults([]);
          setError(e?.message || 'SAP search failed');
        }
      })
      .finally(() => {
        if (!cancelled) {
          setLoading(false);
          setSearched(true);
        }
      });
    return () => {
      cancelled = true;
    };
  }, [soldToSapCode, salesOrganization]);

  // Fallback name search — see the doc comment above the component. Independent of the primary
  // code-based lookup above; only ever shown once that comes up empty.
  const [nameQuery, setNameQuery] = useState('');
  useEffect(() => setNameQuery(docShipToName || ''), [docShipToName]);
  const [nameResults, setNameResults] = useState<SapBusinessPartner[]>([]);
  const [nameLoading, setNameLoading] = useState(false);
  const [nameError, setNameError] = useState<string | null>(null);
  const [nameSearched, setNameSearched] = useState(false);
  const runNameSearch = (term: string) => {
    const q = term.trim();
    if (!q) return;
    setNameLoading(true);
    setNameError(null);
    searchSapBusinessPartner({ name: q })
      .then((r) => {
        if (!Array.isArray(r?.results)) {
          setNameResults([]);
          setNameError('Unexpected response from the server (is the backend up to date?)');
          return;
        }
        setNameResults(r.results);
      })
      .catch((e) => setNameError(e?.message || 'SAP search failed'))
      .finally(() => {
        setNameLoading(false);
        setNameSearched(true);
      });
  };

  const renderNameSearchFallback = () => (
    <div style={{ marginTop: 10, paddingTop: 10, borderTop: '1px solid var(--line-soft)' }}>
      <div className="hint">
        ค้นหา Ship-to จากชื่อโดยตรง — ใช้แทนผลลัพธ์ด้านบนได้ถ้าไม่ถูกต้อง หรือกรณีมีอยู่ใน SAP แล้วแต่ยังไม่ได้ผูกกับ Sold-to นี้:
      </div>
      <div style={{ marginTop: 6, display: 'flex', gap: 8, flexWrap: 'wrap', alignItems: 'center' }}>
        <input
          type="text"
          value={nameQuery}
          onChange={(e) => setNameQuery(e.target.value)}
          placeholder="ชื่อ Ship-to"
          className="txt"
          style={{ minWidth: 220 }}
        />
        <button className="btn sm" onClick={() => runNameSearch(nameQuery)} disabled={nameLoading || !nameQuery.trim()}>
          {nameLoading ? 'กำลังค้นหา…' : 'ค้นหา'}
        </button>
      </div>
      {nameError && <div className="hint">ค้นหาไม่สำเร็จ: {nameError}</div>}
      {nameSearched && !nameLoading && nameResults.length === 0 && (
        <div className="hint" style={{ marginTop: 8 }}>ไม่พบผลลัพธ์</div>
      )}
      {nameResults.length > 0 && (
        <table className="cmp" style={{ marginTop: 8 }}>
          <tbody>
            {nameResults.map((bp) => (
              <tr key={bp.businessPartnerId}>
                <td>
                  <b>{bp.businessPartnerId}</b> — {bp.businessPartnerFullName || bp.businessPartnerName}
                  {(bp.addressCity || bp.addressStreet) && (
                    <div className="hint">{[bp.addressStreet, bp.addressCity].filter(Boolean).join(', ')}</div>
                  )}
                </td>
                <td>
                  <button
                    className="btn sm primary"
                    disabled={disabled}
                    onClick={() =>
                      onUse({
                        customer: soldToSapCode || '',
                        partnerFunction: 'SH',
                        partnerCustomer: bp.businessPartnerId,
                        partner: bp,
                      })
                    }
                  >
                    Use &amp; Save
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );

  const hint = (text: string) => <div className="hint" style={{ padding: '6px 0' }}>{text}</div>;

  if (!soldToSapCode) return hint('A matched customer (with a SAP code) is required first');
  if (loading) return hint('Searching SAP…');

  // The name-search fallback below is now ALWAYS rendered, not only when the automatic
  // partner-function lookup comes up empty. Before this fix, a single (possibly stale or wrong)
  // linked Ship-to hid the search box entirely -- SAP's A_CustSalesPartnerFunc link for this
  // Sold-to can be outdated or simply wrong, and the person had no way to search for a different
  // one short of "+ Add New Ship-to" (a brand-new manual entry, not a re-match). Showing the
  // search box unconditionally lets them override an auto-found match at any time.
  return (
    <div>
      {error && hint('Could not search SAP: ' + error)}
      {!error && searched && results.length === 0 && (
        <div className="hint" style={{ padding: '6px 0' }}>
          No Ship-to found in SAP for this customer — use "+ Add New Ship-to" to enter it manually
        </div>
      )}
      {results.length > 0 && (
        <table className="cmp">
          <tbody>
            {results.map((link) => (
              <tr key={link.partnerCustomer}>
                <td>
                  <b>{link.partnerCustomer}</b>
                  {link.partner && ' — ' + (link.partner.businessPartnerFullName || link.partner.businessPartnerName)}
                  {(link.partner?.addressCity || link.partner?.addressStreet) && (
                    <div className="hint">
                      {[link.partner?.addressStreet, link.partner?.addressCity].filter(Boolean).join(', ')}
                    </div>
                  )}
                </td>
                <td>
                  <button className="btn sm primary" disabled={disabled} onClick={() => onUse(link)}>
                    Use &amp; Save
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {renderNameSearchFallback()}
    </div>
  );
}

/* Ship-to counterpart to ZohoCustomerPanel above, but not a search -- once the Customer/Sold-to
   card has matched an MGT document to a Zoho Account, this reads that SAME Account's own
   "Ship to ..." fields (per Megachem's request: their real Zoho records carry both Sold-to and
   Ship-to address data on one Account) AND, when the Account's "Ship to มากกว่า 1" (Ship_to_1)
   checkbox is ticked, every additional record from Megachem's separate Ship-to Module too --
   showing every raw sub-field on each, not just a collapsed address line. Green Leaf/SAP
   documents now get their own live search instead -- see SapShipToPanel just above. */
function ZohoShipToPanel({
  customerCode,
  masters,
  disabled,
  onUse,
  docShipToName,
  docShipToAddress,
}: {
  customerCode?: string;
  masters: MastersData;
  disabled?: boolean;
  onUse: (info: ZohoShipToInfo, accountId: string) => void;
  /** The document's own Ship-to name/address, used only as the doc side of the AI compare. */
  docShipToName?: string;
  docShipToAddress?: string;
}) {
  // The compatibility alias contains Customer.ComcompyCodeSAP (Zoho Account Code for MGT).
  const accountId: string | undefined = masters.customers.find(
    (c) => c.CustomerCode === customerCode,
  )?.ComcompyCodeSAP;

  const [shipTos, setShipTos] = useState<ZohoShipToInfo[] | null>(null);
  const [hasMultiple, setHasMultiple] = useState(false);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [searched, setSearched] = useState(false);

  useEffect(() => {
    if (!accountId) {
      setShipTos(null);
      setSearched(false);
      return;
    }
    let cancelled = false;
    setLoading(true);
    setError(null);
    getZohoAccountShipTosByCode(accountId)
      .then((r) => {
        if (cancelled) return;
        setShipTos(r?.shipTos ?? []);
        setHasMultiple(!!r?.hasMultipleShipTos);
      })
      .catch((e) => {
        if (!cancelled) setError(e?.message || 'Zoho CRM lookup failed');
      })
      .finally(() => {
        if (!cancelled) {
          setLoading(false);
          setSearched(true);
        }
      });
    return () => {
      cancelled = true;
    };
  }, [accountId]);

  const hint = (text: string) => <div className="hint" style={{ padding: '6px 0' }}>{text}</div>;
  const { ocrProviders } = useMeta();
  const [aiOpen, setAiOpen] = useState(false);

  const addrText = (info: ZohoShipToInfo) =>
    [info.houseNumber, info.street, info.street2, info.district, info.city, info.postCode, info.countryReg]
      .filter((v) => v && v.trim() !== '')
      .join(' ');

  // One Ship-to block (address rows + Use & Save). Shared by the primary account list and the
  // name-search results below. `useAccountCode` is passed through to onUse so a Ship-to picked from
  // a DIFFERENT (affiliated) account still carries that account's code.
  const shipToBlock = (info: ZohoShipToInfo, i: number, useAccountCode: string) => (
    <div key={i} style={i > 0 ? { borderTop: '1px dashed var(--border, #444)', marginTop: 8, paddingTop: 8 } : undefined}>
      {info.source === 'shipto-module' && <div className="hint">From Ship-to Module</div>}
      <AddressFieldRows info={info} />
      <div style={{ padding: '6px 0' }}>
        <button className="btn sm primary" disabled={disabled} onClick={() => onUse(info, useAccountCode)}>
          Use &amp; Save
        </button>
      </div>
    </div>
  );

  // ── Search Ship-to by company name ─────────────────────────────────────────────────────────
  // Per request: a delivery often goes to a different company in the same group, filed in Zoho
  // under its OWN Account (a different name), so besides the billing account's own Ship-tos we let
  // the user search any company by name and pull a Ship-to from that account. Reuses the Account
  // name search + that account's Ship-to list — no new backend endpoint. Always shown.
  const [nameQuery, setNameQuery] = useState('');
  useEffect(() => setNameQuery(docShipToName || ''), [docShipToName]);
  const [accResults, setAccResults] = useState<ZohoAccount[]>([]);
  const [accLoading, setAccLoading] = useState(false);
  const [accError, setAccError] = useState<string | null>(null);
  const [accSearched, setAccSearched] = useState(false);
  const [pickedAcc, setPickedAcc] = useState<ZohoAccount | null>(null);
  const [pickedShipTos, setPickedShipTos] = useState<ZohoShipToInfo[] | null>(null);
  const [pickedLoading, setPickedLoading] = useState(false);
  const [pickedError, setPickedError] = useState<string | null>(null);

  const runNameSearch = (term: string) => {
    const q = term.trim();
    if (!q) return;
    setAccLoading(true); setAccError(null); setPickedAcc(null); setPickedShipTos(null);
    searchZohoAccount({ name: q })
      .then((r) => setAccResults(Array.isArray(r?.results) ? r.results : []))
      .catch((e) => setAccError(e?.message || 'Zoho CRM search failed'))
      .finally(() => { setAccLoading(false); setAccSearched(true); });
  };
  const pickAccount = (acc: ZohoAccount) => {
    setPickedAcc(acc); setPickedShipTos(null); setPickedError(null); setPickedLoading(true);
    getZohoAccountShipTos(acc.accountId)
      .then((r) => setPickedShipTos(r?.shipTos ?? []))
      .catch((e) => setPickedError(e?.message || 'Zoho CRM lookup failed'))
      .finally(() => setPickedLoading(false));
  };

  const renderNameSearch = () => (
    <div style={{ marginTop: 10, paddingTop: 10, borderTop: '1px solid var(--line-soft)' }}>
      <div className="hint">หรือค้นหา Ship-to จากชื่อบริษัท (รวมบริษัทในเครือที่ชื่อไม่ตรงกับผู้ซื้อ):</div>
      <div style={{ marginTop: 6, display: 'flex', gap: 8, flexWrap: 'wrap', alignItems: 'center' }}>
        <input
          type="text"
          value={nameQuery}
          onChange={(e) => setNameQuery(e.target.value)}
          onKeyDown={(e) => { if (e.key === 'Enter') runNameSearch(nameQuery); }}
          placeholder="ชื่อบริษัท / Ship-to"
          className="txt"
          style={{ minWidth: 220 }}
        />
        <button className="btn sm" onClick={() => runNameSearch(nameQuery)} disabled={accLoading || !nameQuery.trim()}>
          {accLoading ? 'กำลังค้นหา…' : 'ค้นหา'}
        </button>
      </div>
      {accError && <div className="hint">ค้นหาไม่สำเร็จ: {accError}</div>}
      {accSearched && !accLoading && accResults.length === 0 && (
        <div className="hint" style={{ marginTop: 8 }}>ไม่พบบริษัทใน Zoho CRM</div>
      )}
      {accResults.length > 0 && (
        <table className="cmp" style={{ marginTop: 8 }}>
          <tbody>
            {accResults.map((acc) => (
              <tr key={acc.accountId}>
                <td><b>{acc.accountCode || acc.accountId}</b> — {acc.accountName}</td>
                <td>
                  <button
                    className={'btn sm ' + (pickedAcc?.accountId === acc.accountId ? 'primary' : '')}
                    onClick={() => pickAccount(acc)}
                    disabled={pickedLoading && pickedAcc?.accountId === acc.accountId}
                  >
                    ดู Ship-to
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {pickedAcc && (
        <div style={{ marginTop: 8 }}>
          <div className="hint">Ship-to ของ {pickedAcc.accountName}:</div>
          {pickedLoading && <div className="hint">กำลังโหลด Ship-to…</div>}
          {pickedError && <div className="hint">โหลดไม่สำเร็จ: {pickedError}</div>}
          {pickedShipTos && pickedShipTos.length === 0 && <div className="hint">บริษัทนี้ไม่มี Ship-to ใน Zoho</div>}
          {pickedShipTos && pickedShipTos.map((info, i) =>
            shipToBlock(info, i, pickedAcc.accountCode || pickedAcc.accountId),
          )}
        </div>
      )}
    </div>
  );

  if (!customerCode) return hint('A customer must be specified first');

  const aiCandidates: CompareCandidateItem<ZohoShipToInfo>[] = (shipTos ?? []).map((info, i) => ({
    id: info.code || `ship-${i}`,
    label: info.code || (info.source === 'shipto-module' ? `Ship-to #${i + 1}` : 'Account address'),
    fields: [
      { label: 'Address', value: addrText(info) },
      { label: 'City', value: info.city },
      { label: 'Post Code', value: info.postCode },
      { label: 'Source', value: info.source === 'shipto-module' ? 'Ship-to Module' : 'Account' },
    ],
    raw: info,
  }));

  // The billing account's own Ship-tos (primary) come first; the name search is ALWAYS shown below
  // so a Ship-to under a different affiliated company can be found even when this account has some.
  return (
    <>
      {!accountId && hint('This customer is not linked to a Zoho account')}
      {accountId && loading && hint('Reading Ship-to info from Zoho CRM…')}
      {accountId && error && hint('Could not read Zoho CRM: ' + error)}
      {accountId && !loading && !error && searched && (!shipTos || shipTos.length === 0) &&
        hint('No Ship-to on this account — search by company name below, or use "+ Add New Ship-to"')}
      {accountId && shipTos && shipTos.length > 0 && (
        <>
          {hasMultiple && shipTos.length > 1 && (
            <div className="hint" style={{ padding: '6px 0', display: 'flex', alignItems: 'center', gap: 8 }}>
              <span>This account is marked "Ship to มากกว่า 1" — {shipTos.length} delivery addresses on file:</span>
              <button className="btn sm" onClick={() => setAiOpen(true)}>
                <i className="fa-solid fa-wand-magic-sparkles" /> Ask AI to match
              </button>
            </div>
          )}
          {shipTos.map((info, i) => shipToBlock(info, i, accountId))}
        </>
      )}
      {renderNameSearch()}
      <CompareModal<ZohoShipToInfo>
        open={aiOpen}
        onClose={() => setAiOpen(false)}
        title="AI Suggested Match — Ship-to (Zoho CRM)"
        docFields={[
          { label: 'Ship-to Name (from document)', value: docShipToName },
          { label: 'Ship-to Address (from document)', value: docShipToAddress },
        ]}
        candidates={aiCandidates}
        providers={ocrProviders || []}
        onUse={(info) => {
          onUse(info, accountId || '');
          setAiOpen(false);
        }}
      />
    </>
  );
}

/* Read-only counterpart to ZohoShipToPanel -- shows the Sold-to (billing) address off the same
   matched Zoho Account, every raw sub-field, purely as a side-by-side confirmation next to the
   document's own customerAddress. No "Use & Save" here: unlike Ship-to, there's no separate
   local master row to create -- the Sold-to address already lives on the Customer match itself. */
function ZohoSoldToAddressPanel({ customerCode, masters }: { customerCode?: string; masters: MastersData }) {
  const accountId: string | undefined = masters.customers.find(
    (c) => c.CustomerCode === customerCode,
  )?.ComcompyCodeSAP;

  const [info, setInfo] = useState<ZohoShipToInfo | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!accountId) {
      setInfo(null);
      return;
    }
    let cancelled = false;
    setLoading(true);
    setError(null);
    getZohoAccountSoldToByCode(accountId)
      .then((r) => {
        if (!cancelled) setInfo(r ?? null);
      })
      .catch((e) => {
        if (!cancelled) setError(e?.message || 'Zoho CRM lookup failed');
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [accountId]);

  if (!accountId) return <span className="hint">This customer is not linked to a Zoho account</span>;
  if (loading) return <span className="hint">Reading Sold-to address from Zoho CRM…</span>;
  if (error) return <span className="hint">Could not read Zoho CRM: {error}</span>;
  if (!info) return <span className="hint">(no address on file)</span>;
  return <AddressFieldRows info={info} />;
}

// Credit limit / balance / payment terms / payment currency / Incoterms straight off the Zoho
// Account record (fields confirmed against the manual, section 5.2.8 "Credit Information") --
// reuses the same getZohoAccountFull fetch already used by the "Compare with AI" popup, just
// filtered down to these labels, so no new backend endpoint is needed.
function ZohoCreditInfoPanel({ customerCode, masters }: { customerCode?: string; masters: MastersData }) {
  const accountId: string | undefined = masters.customers.find(
    (c) => c.CustomerCode === customerCode,
  )?.ComcompyCodeSAP;

  const [fields, setFields] = useState<ZohoFullField[] | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!accountId) {
      setFields(null);
      return;
    }
    let cancelled = false;
    setLoading(true);
    setError(null);
    getZohoAccountFullByCode(accountId)
      .then((r) => {
        if (!cancelled) setFields(r.fields);
      })
      .catch((e) => {
        if (!cancelled) setError(e?.message || 'Zoho CRM lookup failed');
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [accountId]);

  if (!accountId) return <span className="hint">This customer is not linked to a Zoho account</span>;
  if (loading) return <span className="hint">Reading credit/payment info from Zoho CRM…</span>;
  if (error) return <span className="hint">Could not read Zoho CRM: {error}</span>;

  const pick = (labelContains: string) =>
    fields?.find((f) => f.label.toLowerCase().includes(labelContains.toLowerCase()))?.value;

  const rows: [string, string | null | undefined][] = [
    ['Credit Limit', pick('Credit Limited')],
    ['Remaining Balance', pick('Remaining Balance')],
    ['Outstanding Amount', pick('Outstanding Amount')],
    ['Payment Terms', pick('Payment Terms')],
    ['Payment Currency', pick('Payment Currency')],
    ['Incoterms', pick('Incoterms')],
  ];
  if (!rows.some(([, v]) => v)) return <span className="hint">(no credit/payment data on file in Zoho)</span>;

  return (
    <table className="cmp">
      <tbody>
        {rows.map(([label, value]) =>
          value ? (
            <tr key={label}>
              <td className="hint">{label}</td>
              <td>{value}</td>
            </tr>
          ) : null,
        )}
      </tbody>
    </table>
  );
}

// GLC (SAP) Customer card, right-hand "Data from SAP" column. Renders the SAP customer fields, but
// fills them LIVE from the SAP customer master and lets the person pick a SALES AREA:
//  - A customer can have several sales areas (differing by Distribution Channel / Division), each
//    with its own Sales Group / Payment Terms. When there's exactly ONE it's used automatically;
//    when there are MORE than one, a small picker appears (so the person chooses which one) — and the
//    chosen area's Channel / Division / Sales Group get persisted onto the header (onSetSalesArea)
//    so the SAP payload uses the RIGHT ones, not "the first area found".
//  - "Sales Org / Channel / Div" and "Sales Group" rows reflect the selected area.
//  - "Payment Terms" shows the selected area's CustomerPaymentTerms, falling back to the company-level
//    lookup; the code (e.g. "5009") is mapped to text via dbo.SysDataMapping (masters.paymentterms).
// All best-effort and inline (no separate card): any failure/empty just shows a hint or "—", never
// blocks. salesOrg follows the company being worked as (GLC = 2000).
function SapCustomerSapSide({
  r,
  salesOrg,
  companyCode,
  masters,
  header,
  posted,
  onSetSalesArea,
}: {
  r: MapEntry;
  salesOrg?: string;
  companyCode?: string;
  masters: MastersData;
  header: DocHeader;
  posted: boolean;
  onSetSalesArea: (fields: { distChannel?: string; division?: string; salesGroup?: string }) => void;
}) {
  const soldToSapCode = r.sapCode;
  const [pt, setPt] = useState<SapCustomerPaymentTerms | null>(null);
  const [ptStatus, setPtStatus] = useState<'idle' | 'loading' | 'error'>('idle');
  const [areas, setAreas] = useState<SapCustomerSalesArea[]>([]);
  const [areaStatus, setAreaStatus] = useState<'idle' | 'loading' | 'error'>('idle');
  const [selectedKey, setSelectedKey] = useState('');
  const lastAppliedRef = useRef<string | null>(null);

  const areaKey = (a: SapCustomerSalesArea) => `${a.distributionChannel}|${a.division}`;

  // Company-level payment terms (fallback for areas that carry none of their own).
  useEffect(() => {
    if (!soldToSapCode) {
      setPt(null);
      setPtStatus('idle');
      return;
    }
    let cancelled = false;
    setPtStatus('loading');
    getSapCustomerPaymentTerms(soldToSapCode, salesOrg, companyCode)
      .then((res) => {
        if (!cancelled) {
          setPt(res.paymentTerms);
          setPtStatus('idle');
        }
      })
      .catch(() => {
        if (!cancelled) setPtStatus('error');
      });
    return () => {
      cancelled = true;
    };
  }, [soldToSapCode, salesOrg, companyCode]);

  // The customer's sales areas for this sales org.
  useEffect(() => {
    if (!soldToSapCode) {
      setAreas([]);
      setAreaStatus('idle');
      return;
    }
    let cancelled = false;
    setAreaStatus('loading');
    getSapCustomerSalesAreas(soldToSapCode, salesOrg)
      .then((res) => {
        if (!cancelled) {
          setAreas(res.areas || []);
          setAreaStatus('idle');
        }
      })
      .catch(() => {
        if (!cancelled) setAreaStatus('error');
      });
    return () => {
      cancelled = true;
    };
  }, [soldToSapCode, salesOrg]);

  // Initial selection: the area matching what's already on the header, else the first. Keeps a valid
  // user selection across re-renders. (Only re-computes when the area list itself changes.)
  useEffect(() => {
    if (!areas.length) return;
    setSelectedKey((prev) => {
      if (prev && areas.some((a) => areaKey(a) === prev)) return prev;
      const match = areas.find(
        (a) =>
          a.distributionChannel === ((header?.distChannel as string) ?? '') &&
          a.division === ((header?.division as string) ?? ''),
      );
      return areaKey(match || areas[0]);
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [areas]);

  const selectedArea = areas.find((a) => areaKey(a) === selectedKey) || areas[0] || null;

  // Persist the selected area's Channel / Division / Sales Group onto the header (so the payload uses
  // them), but only when they differ from what's already there — and remember the last requested
  // signature so we don't fire again while the re-map is in flight.
  useEffect(() => {
    if (posted || !selectedArea) return;
    const sig = `${selectedArea.distributionChannel}|${selectedArea.division}|${selectedArea.salesGroup || ''}`;
    const cur = `${(header?.distChannel as string) ?? ''}|${(header?.division as string) ?? ''}|${(header?.salesGroup as string) ?? ''}`;
    if (cur === sig) {
      lastAppliedRef.current = sig;
      return;
    }
    if (lastAppliedRef.current === sig) return;
    lastAppliedRef.current = sig;
    onSetSalesArea({
      distChannel: selectedArea.distributionChannel,
      division: selectedArea.division,
      salesGroup: selectedArea.salesGroup || '',
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [selectedKey, areas, posted, header?.distChannel, header?.division, header?.salesGroup]);

  // Payment terms to show: the selected area's own, else the company-level lookup. Map code -> text.
  const ptCode = (selectedArea?.customerPaymentTerms?.trim() || pt?.paymentTerms?.trim()) || undefined;
  const ptDesc = ptCode
    ? ((masters.paymentterms || []).find(
        (m) =>
          String(m.Code ?? '').trim() === ptCode &&
          (m.IsActive === undefined || m.IsActive === null || m.IsActive === true || m.IsActive === 1),
      )?.Text as string | undefined)
    : undefined;
  const ptValue =
    !soldToSapCode ? ''
    : ptStatus === 'loading' && !ptCode ? 'กำลังอ่านจาก SAP…'
    : ptStatus === 'error' && !ptCode ? 'อ่านจาก SAP ไม่ได้'
    : ptCode ? (ptDesc || `${ptCode} (ยังไม่ได้ตั้งคำอธิบาย)`)
    : '';

  const sgValue = selectedArea?.salesGroup || '';
  const socdValue = selectedArea
    ? `${salesOrg || '—'} / ${selectedArea.distributionChannel || '—'} / ${selectedArea.division || '—'}`
    : undefined;

  // Build the SAP field list: reflect the selected area in Sales Org/Channel/Div + Payment Terms, and
  // add a Sales Group row.
  let items = (r.sap || []).map((f) => {
    if (f.label === 'Sales Org / Channel / Div' && socdValue) return { ...f, value: socdValue };
    if (f.label === 'Payment Terms') return { ...f, value: ptValue };
    return f;
  });
  if (ptValue && !items.some((f) => f.label === 'Payment Terms'))
    items = [...items, { label: 'Payment Terms', value: ptValue } as MapField];
  if (items.some((f) => f.label === 'Sales Group'))
    items = items.map((f) => (f.label === 'Sales Group' ? { ...f, value: sgValue } : f));
  else items = [...items, { label: 'Sales Group', value: sgValue } as MapField];

  return (
    <>
      {areas.length > 1 && (
        <div className="f" style={{ marginBottom: 8 }}>
          <label>เลือก Sales Area (ลูกค้ารายนี้มี {areas.length} area)</label>
          <select value={selectedKey} onChange={(e) => setSelectedKey(e.target.value)} disabled={posted}>
            {areas.map((a) => (
              <option key={areaKey(a)} value={areaKey(a)}>
                Channel {a.distributionChannel || '—'} / Division {a.division || '—'}
                {a.salesGroup ? ` · Sales Group ${a.salesGroup}` : ''}
              </option>
            ))}
          </select>
          <small className="master-field-help">
            ลูกค้ามีหลาย sales area — เลือกให้ตรงกับที่ต้องการ ระบบจะใช้ Channel/Division/Sales Group ของ area นี้ส่งไป SAP
          </small>
        </div>
      )}
      {areaStatus === 'loading' && <div className="hint" style={{ padding: '2px 0' }}>กำลังอ่าน Sales Area จาก SAP…</div>}
      <SideList items={items} side="sap" />
    </>
  );
}

// Every open (not Closed Won/Lost) Deal linked to this Zoho Account, with its Deal Items
// subform -- read-only listing. Megachem's call: show every candidate rather than the system
// narrowing to "the" match, so the person (optionally aided by the OCR AI-compare tool) picks
// which Deal a document belongs to before a Sales Order gets created in Zoho against it.
/* One row of a RowMapTable -- the SAME field on the SAME row for both sides, so a mismatch is
   spotted by scanning across instead of reading two independent lists and cross-checking them
   by eye. match: true = agree (green dot), false = disagree (red dot + tinted row), null/
   undefined = not evaluated (e.g. one side has nothing to compare, or the field is informational
   only) -> a neutral blank dot, no tint. */
interface RowMapRow {
  label: string;
  docValue?: string | number | null;
  srcValue?: string | number | null;
  match?: boolean | null;
}

function rowMapDisplay(v: string | number | null | undefined): React.ReactNode {
  if (v === null || v === undefined || v === '') return <span className="hint">—</span>;
  return v;
}

// Tolerant numeric compare (handles "1,234.50"-style strings from OCR/Zoho alike) -- null when
// either side can't be parsed as a number at all, so an unevaluated field never renders as a
// false mismatch.
function numEq(a: unknown, b: unknown, eps = 0.01): boolean | null {
  const toNum = (v: unknown) => {
    if (typeof v === 'number') return v;
    const s = (v ?? '').toString().replace(/,/g, '').trim();
    return s === '' ? NaN : parseFloat(s);
  };
  const na = toNum(a);
  const nb = toNum(b);
  if (Number.isNaN(na) || Number.isNaN(nb)) return null;
  return Math.abs(na - nb) <= eps;
}

// Case/whitespace-insensitive string compare -- null (not false) when either side is blank, so
// a field neither side filled in doesn't render as a mismatch.
function strEq(a: unknown, b: unknown): boolean | null {
  const sa = (a ?? '').toString().trim();
  const sb = (b ?? '').toString().trim();
  if (!sa || !sb) return null;
  return sa.toLowerCase() === sb.toLowerCase();
}

/* Field-by-field comparison table: Field | Document value | status dot | Source value, all on
   one row per field. Used by the Deal card below for both the header-level comparison and each
   line's Deal Item comparison -- see ZohoDealCard. */
function RowMapTable({
  rows,
  docLabel = 'Document',
  srcLabel = 'Source',
}: {
  rows: RowMapRow[];
  docLabel?: string;
  srcLabel?: string;
}) {
  return (
    <table className="rowmap">
      <thead>
        <tr>
          <th>Field</th>
          <th>{docLabel}</th>
          <th className="rowmap-mid" />
          <th>{srcLabel}</th>
        </tr>
      </thead>
      <tbody>
        {rows.map((row, i) => (
          <tr key={i} className={row.match === false ? 'rowmap-row-bad' : undefined}>
            <td className="rowmap-field">{row.label}</td>
            <td>{rowMapDisplay(row.docValue)}</td>
            <td className="rowmap-mid">
              {row.match === true && <span className="rowmap-dot ok">✓</span>}
              {row.match === false && <span className="rowmap-dot mismatch">✕</span>}
              {(row.match === null || row.match === undefined) && <span className="rowmap-dot blank">–</span>}
            </td>
            <td className="rowmap-src">{rowMapDisplay(row.srcValue)}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

/* Top-level "Deal" card (MGT/Zoho only), promoted out of the Customer card's sub-sections so the
   person can actually compare this document's line items against a Deal's Deal Items -- the
   real next step before creating a Sales Order -- field by field, instead of just reading a flat
   list next to (not lined up with) the document. Defaults to the only open Deal when there's
   just one; with more than one, the person picks which to compare against. Each document line is
   matched to a Deal Item by Material Code first (the code the Material — Row N step above already
   resolved, kept completely separate from and unaffected by this card), falling back to a loose
   name/description match when no code is available on either side. Purely informational --
   unlike the Material comparison, it doesn't feed map.pass/map.errors. */
function ZohoDealCard({
  no,
  customerCode,
  doc,
  map,
  selectedId,
  onSelectId,
  onDealResolved,
}: {
  no: number;
  customerCode?: string;
  doc: DocModel;
  map: MapResult;
  /** Controlled from DocumentPage (rather than local state) so the "Send to Zoho" confirmation
   *  knows which Deal is currently picked. */
  selectedId: string;
  onSelectId: (id: string) => void;
  /** Reports the full resolved Deal record (incl. its Deal Items) up to MappingCards as soon as
   *  it's known, so the "Material — Row N" cards below can offer the AI-suggested-match tool
   *  against this Deal's own Items without re-fetching the Deals list themselves. */
  onDealResolved?: (deal: ZohoDeal | null) => void;
}) {
  // Looked up by the customer's own code (Account_Code on the Deal record) -- deliberately NOT
  // the Zoho Account id used by ZohoSoldToAddressPanel/ShipTo above, so this works even when the
  // Master Customer row's Zoho account link is missing or stale (see ZohoDealClient.
  // FindOpenDealsByAccountCodeAsync).
  const [deals, setDeals] = useState<ZohoDeal[] | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!customerCode) {
      setDeals(null);
      onSelectId('');
      return;
    }
    let cancelled = false;
    setLoading(true);
    setError(null);
    getZohoDealsByAccountCode(customerCode)
      .then((r) => {
        if (cancelled) return;
        setDeals(r.deals);
        onSelectId(r.deals.some((d) => d.id === selectedId) ? selectedId : r.deals[0]?.id || '');
      })
      .catch((e) => {
        if (!cancelled) setError(e?.message || 'Zoho CRM lookup failed');
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [customerCode]);

  const hint = (text: string) => <div className="hint" style={{ padding: '6px 0' }}>{text}</div>;
  const deal = deals?.find((d) => d.id === selectedId);
  const { ocrProviders } = useMeta();
  const [aiOpen, setAiOpen] = useState(false);

  useEffect(() => {
    onDealResolved?.(deal ?? null);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [deal]);

  // "Ask AI to match" for the Deal: give the AI the document's own header (Customer PO, delivery
  // date) plus a one-line summary of its materials, and let it judge which open Deal this document
  // belongs to. The person still confirms by clicking "Use this record" -- the AI only advises.
  const dealDocFields: CompareField[] = [
    { label: 'Customer PO No.', value: doc.header.poNo },
    { label: 'Delivery Date', value: doc.header.deliveryDate },
    { label: 'Materials (from document)', value: doc.lines.map((l) => l.desc || l.extCode).filter(Boolean).join('; ') },
  ];
  const dealCandidates: CompareCandidateItem<ZohoDeal>[] = (deals || []).map((d) => ({
    id: d.id,
    label: d.dealName,
    fields: [
      { label: 'Deal Name', value: d.dealName },
      { label: 'Stage', value: d.stage },
      { label: 'Customer PO / Ref', value: d.customerRef },
      { label: 'Delivery Date', value: d.deliveryDate },
      { label: 'Materials (Deal Items)', value: d.items.map((it) => it.materialName || it.materialCode).filter(Boolean).join('; ') },
    ],
    raw: d,
  }));

  return (
    <div className="cmp-card">
      <div className="cmp-head">
        <span className="cmp-no">{no}</span>
        <b>Deal (Zoho)</b>
        {loading ? (
          <span className="badge b-idle">Reading…</span>
        ) : !customerCode ? (
          <span className="badge b-idle">Waiting for customer</span>
        ) : deals && deals.length === 0 ? (
          <span className="badge b-fail"><i className="fa-solid fa-xmark" /> No open Deal</span>
        ) : deal ? (
          <span className="badge b-ok"><i className="fa-solid fa-check" /> {deal.stage}</span>
        ) : null}
        <span className="hint">Next step: pick the Deal this document's Sales Order belongs to</span>
        <div className="sp" />
        {deals && deals.length > 1 && (
          <select className="cmp-pick" value={selectedId} onChange={(e) => onSelectId(e.target.value)}>
            {deals.map((d) => (
              <option key={d.id} value={d.id}>
                {d.dealName}
              </option>
            ))}
          </select>
        )}
        {deals && deals.length > 1 && (
          <button className="btn sm" style={{ marginLeft: 6 }} onClick={() => setAiOpen(true)}>
            <i className="fa-solid fa-wand-magic-sparkles" /> Ask AI to match
          </button>
        )}
      </div>
      <div className="cmp-body" style={{ display: 'block' }}>
        {!customerCode
          ? hint('A customer must be specified first')
          : loading
          ? hint('Reading open Deals from Zoho CRM…')
          : error
          ? hint('Could not read Zoho CRM: ' + error)
          : !deals || deals.length === 0
          ? hint('No open Deal found for this customer in Zoho CRM')
          : deal && (
              <>
                <RowMapTable
                  docLabel="Document"
                  srcLabel={deal.dealName}
                  rows={[
                    {
                      label: 'Customer PO No.',
                      docValue: doc.header.poNo,
                      srcValue: deal.customerRef,
                      match: strEq(doc.header.poNo, deal.customerRef),
                    },
                    {
                      label: 'Delivery Date',
                      docValue: doc.header.deliveryDate,
                      srcValue: deal.deliveryDate,
                      match: strEq(doc.header.deliveryDate, deal.deliveryDate),
                    },
                  ]}
                />
                {deal.items.length === 0 ? (
                  hint('This Deal has no Deal Items')
                ) : (
                  <div className="rowmap-group">
                    <div className="rowmap-group-title">Deal Items vs Document Lines</div>
                    {doc.lines.map((line, i) => {
                      const lineCode = map.lines[i]?.code || line.materialCode || '';
                      let item: (typeof deal.items)[number] | undefined;
                      let matchedByCode = false;
                      if (lineCode) {
                        item = deal.items.find((it) => it.materialCode && it.materialCode.toLowerCase() === lineCode.toLowerCase());
                        if (item) matchedByCode = true;
                      }
                      if (!item && line.desc) {
                        const desc = line.desc.toLowerCase();
                        item = deal.items.find(
                          (it) =>
                            (it.materialName && (it.materialName.toLowerCase().includes(desc) || desc.includes(it.materialName.toLowerCase()))) ||
                            (it.materialDescription &&
                              (it.materialDescription.toLowerCase().includes(desc) || desc.includes(it.materialDescription.toLowerCase()))),
                        );
                      }
                      return (
                        <div key={i} className="rowmap-group">
                          <div className="hint" style={{ marginBottom: 4 }}>
                            Line {i + 1}: {line.desc || line.extCode || '—'}
                          </div>
                          {item ? (
                            <RowMapTable
                              docLabel="Document Line"
                              srcLabel="Deal Item"
                              rows={[
                                {
                                  label: 'Material',
                                  docValue: line.desc || line.extCode,
                                  srcValue: item.materialName || item.materialCode,
                                  match: matchedByCode ? true : null,
                                },
                                {
                                  label: 'Quantity',
                                  docValue: qtyTxt(line.qty),
                                  srcValue: item.quantity != null ? qtyTxt(item.quantity) : null,
                                  match: numEq(line.qty, item.quantity),
                                },
                                { label: 'Unit', docValue: line.uom, srcValue: item.unit, match: strEq(line.uom, item.unit) },
                                { label: 'Unit Price', docValue: line.price, srcValue: item.unitPrice, match: numEq(line.price, item.unitPrice) },
                                { label: 'Amount', docValue: line.amount, srcValue: item.totalAmount, match: numEq(line.amount, item.totalAmount) },
                              ]}
                            />
                          ) : (
                            hint('No matching Deal Item found for this line')
                          )}
                        </div>
                      );
                    })}
                  </div>
                )}
              </>
            )}
      </div>
      <CompareModal<ZohoDeal>
        open={aiOpen}
        onClose={() => setAiOpen(false)}
        title="AI Suggested Match — which Deal does this document belong to?"
        docFields={dealDocFields}
        candidates={dealCandidates}
        providers={ocrProviders || []}
        onUse={(d) => {
          onSelectId(d.id);
          setAiOpen(false);
        }}
      />
    </div>
  );
}

function qtyTxt(n: unknown) {
  return num(n).toLocaleString('en-US', { maximumFractionDigits: 3 });
}

/* GLC Sales Order, per line: pick which sales person handled/verified THIS line's mapping. The value
   is a SAP custom Person ID (YY1_SDSalesEmployeeI_SDI). It's auto-filled from the Sales Order history
   of this customer+material (shown as the "จากประวัติ" hint) and can be changed here from the full
   list (DB master + live SAP). Person IDs that aren't in the list yet (the current value, or the
   suggestion) are still offered so nothing is lost. */
function SalesEmployeePicker({
  value,
  suggestion,
  list,
  disabled,
  onChange,
}: {
  value: string;
  suggestion?: SapSalesEmployeeSuggestion | null;
  list: SapSalesEmployee[];
  disabled?: boolean;
  onChange: (personId: string) => void;
}) {
  const nameById = new Map<string, string>();
  for (const e of list) if (e.name) nameById.set(e.personId, e.name);
  if (suggestion?.personId && suggestion.name) nameById.set(suggestion.personId, suggestion.name);

  const label = (id: string) => {
    const nm = nameById.get(id);
    return nm ? `${id} — ${nm}` : id;
  };

  // Options: every list entry, plus the current value and the suggestion if they're not in the list.
  const ids: string[] = [];
  const seen = new Set<string>();
  const add = (id?: string | null) => { if (id && !seen.has(id)) { seen.add(id); ids.push(id); } };
  add(value);
  if (suggestion?.personId) add(suggestion.personId);
  for (const e of list) add(e.personId);

  const suggestionMatches = !!suggestion?.personId && suggestion.personId === value;

  return (
    <div className="cmp-sub">
      <div className="cmp-head">
        <b>ผู้ขาย (Sales) — รายการนี้</b>
        {value ? (
          <span className="badge b-ok" style={{ marginLeft: 8 }}>
            <i className="fa-solid fa-user-check" /> {label(value)}
          </span>
        ) : (
          <span className="badge b-idle" style={{ marginLeft: 8 }}>ยังไม่ได้เลือก</span>
        )}
      </div>
      <div className="cmp-body" style={{ display: 'block' }}>
        <select
          value={value}
          disabled={disabled}
          onChange={(e) => onChange(e.target.value)}
          style={{ maxWidth: 360, width: '100%' }}
        >
          <option value="">-- เลือกผู้ขาย --</option>
          {ids.map((id) => (
            <option key={id} value={id}>{label(id)}</option>
          ))}
        </select>
        {suggestion?.personId ? (
          <small className="master-field-help">
            จากประวัติ: <b>{label(suggestion.personId)}</b>
            {suggestion.salesOrder ? ` · SO ${suggestion.salesOrder}` : ''}
            {suggestion.creationDate ? ` · ${suggestion.creationDate}` : ''}
            {!suggestionMatches && !disabled && (
              <>
                {' '}
                <button className="btn sm" style={{ marginLeft: 6 }} onClick={() => onChange(suggestion.personId)}>
                  ใช้ค่านี้
                </button>
              </>
            )}
          </small>
        ) : (
          <small className="master-field-help">
            ไม่พบประวัติผู้ขายของลูกค้ารายนี้กับสินค้านี้ — เลือกจากรายชื่อทั้งหมด
          </small>
        )}
      </div>
    </div>
  );
}

interface Props {
  doc: DocModel;
  map: MapResult;
  masters: MastersData;
  companyCode: string;
  plant: string;
  posted: boolean;
  onManualHeader: (key: string, value: string) => void;
  /** GLC: persist SO header fields onto the document (so the SAP payload uses them) and re-map —
   *  the chosen sales area's DistributionChannel / Division / Sales Group. (The Sales Employee is now
   *  recorded per line via onSetLineSalesEmployee below, not as a header override.) */
  onSetSalesArea: (fields: { distChannel?: string; division?: string; salesGroup?: string }) => void;
  onManualLine: (i: number, value: string) => void;
  onQuickAddVendor: () => void;
  onQuickAddCustomer: () => void;
  onQuickAddShipTo: () => void;
  onQuickAddMaterial: (i: number) => void;
  onUseSapMaterial: (i: number, material: SapMaterial) => void;
  onAddUomRule: (i: number) => void;
  /** GLC Sales Order only: the full Sales Employee pick list (DB master + live SAP), the per-line
   *  history suggestion (who handled this customer+material last time), and the setter that stores
   *  the chosen Person ID on the line (extra.salesEmployee → SAP item field YY1_SDSalesEmployeeI_SDI). */
  salesEmployees?: SapSalesEmployee[];
  lineSalesEmpSuggest?: Record<number, SapSalesEmployeeSuggestion | null>;
  onSetLineSalesEmployee?: (i: number, personId: string) => void;
  /** "ดึงหน่วยแปลงจาก SAP" — for a material that's already matched locally but has no/wrong Unit
   *  Conversion rule: fetches SAP's live pack size for the matched material and opens the same
   *  editable confirm-and-save popup as onUseSapMaterial's step 2, pre-filled with it. Unlike
   *  onAddUomRule (blank/manual entry), this one calls out to SAP first — the automatic-fetch
   *  counterpart for materials that never go through the "Data from SAP" panel below because
   *  they're already matched (that panel only renders when r.status === 'fail'). */
  onFetchSapUom?: (i: number) => void;
  onUseSapCustomer: (bp: SapBusinessPartner) => void;
  onUseZohoAccount: (acc: ZohoAccount) => void;
  /** true when the signed-in user's company is MGT — routes Customer matching to Zoho CRM
   *  instead of SAP (see DocumentPage, which reads primaryCompany.companyCode via AppLayout's
   *  Outlet context). */
  isMgt?: boolean;
  /** Called when a Customer search finds more than one candidate — DocumentPage turns this into
   *  a message in the existing "Chat to Fix Data" box so the person can resolve it by chatting,
   *  as well as (still) by clicking "Use & Save" on one of the rows shown directly below. */
  onProposeMatch?: (proposal: CustomerMatchProposal) => void;
  /** Called when a Customer search stops being ambiguous (0 or 1 result) — clears any pending
   *  chat-driven match question so it doesn't linger once it's moot. */
  onClearMatch?: () => void;
  /** MGT/Zoho only: creates the local Ship-to master row from the matched Customer's own Zoho
   *  Account ship-to fields and applies it — see ZohoShipToPanel. */
  onUseZohoShipTo?: (info: ZohoShipToInfo, accountId: string) => void;
  /** GLC/SAP only: creates the local Ship-to master row from a chosen SAP
   *  A_CustSalesPartnerFunc partner-function link and applies it — see SapShipToPanel. */
  onUseSapShipTo?: (link: SapPartnerFunctionLink) => void;
  /** MGT/Zoho only: which Deal (by Zoho record id) is currently picked on the Deal card --
   *  lifted up to DocumentPage so its "Send to Zoho" confirmation knows which Deal to link the
   *  new Sales Order to. */
  selectedDealId?: string;
  onSelectDeal?: (dealId: string) => void;
  /** MGT/Zoho only: result of GET .../sales-order/preview for the selected Deal, fetched by
   *  DocumentPage as soon as a Deal is picked (independent of opening the Send-to-Zoho confirm
   *  modal). Drives the "Material — Row N" cards below: for an MGT document, material matching
   *  happens against this Deal's own Ordered Items in Zoho, not the local SAP-sourced Material
   *  Master, so the card shows this instead of the usual r.sap SAP comparison. */
  /** MGT/Zoho only: the full resolved Deal (with its Deal Items), reported up so DocumentPage can
   *  drive the inline Sales Order editor (which replaced the per-line "Material — Row N" cards). */
  onDealResolved?: (deal: ZohoDeal | null) => void;
  /** MGT/Zoho only: the document's own "DETAIL — Line Items" table, rendered inside this card
   *  (instead of as a separate card) so all of the document-vs-Zoho review sits in one place. */
  detailContent?: React.ReactNode;
  /** MGT/Zoho only: the matched Deal's Ordered Items, shown as the material candidates in the
   *  per-line Material cards (there is no global Zoho material search). */
  dealItems?: ZohoDealItem[];
  /** MGT/Zoho only: pick a Deal Ordered Item for line i and save it as a CustomerMaterial mapping. */
  onUseZohoMaterial?: (i: number, item: ZohoDealItem) => void;
}

export default function MappingCards({
  doc,
  map,
  masters,
  companyCode,
  plant,
  posted,
  onManualHeader,
  onSetSalesArea,
  onManualLine,
  onQuickAddVendor,
  onQuickAddCustomer,
  onQuickAddShipTo,
  onQuickAddMaterial,
  onUseSapMaterial,
  onAddUomRule,
  salesEmployees,
  lineSalesEmpSuggest,
  onSetLineSalesEmployee,
  onFetchSapUom,
  onUseSapCustomer,
  onUseZohoAccount,
  isMgt,
  onProposeMatch,
  onClearMatch,
  onUseZohoShipTo,
  onUseSapShipTo,
  selectedDealId,
  onSelectDeal,
  onDealResolved,
  detailContent,
  dealItems,
  onUseZohoMaterial,
}: Props) {
  const { ocrProviders } = useMeta();
  const [vendorAiOpen, setVendorAiOpen] = useState(false);
  // External searches are opened explicitly by the person, regardless of mapping status. This
  // keeps Customer, Ship-to and Material consistent: first click Search, then inspect the initial
  // result and optionally type another query inside the opened panel.
  const [customerSapOpen, setCustomerSapOpen] = useState(false);
  const [shipToSapOpen, setShipToSapOpen] = useState(false);
  // Same idea per Material line: the live SAP/Zoho material search panel normally only shows for a
  // line whose match failed. This lets a person open it on demand for an already-matched line too
  // (to re-search SAP/Zoho and pick a different material), keyed by line index.
  const [matSapOpen, setMatSapOpen] = useState<Record<number, boolean>>({});
  // "Ask AI to match" for the Vendor card: unlike Customer/Ship-to (which search a live external
  // system), the vendor master is local, so the candidate pool is masters.vendors -- narrowed to
  // rows whose name loosely overlaps the document's vendor name (either direction), and capped so
  // the AI isn't handed the entire master. Falls back to the first rows when nothing overlaps.
  const vendorAiCandidates: CompareCandidateItem<Record<string, unknown>>[] = (() => {
    const dn = (doc.header.vendorName || '').toLowerCase().trim();
    const all = masters.vendors;
    const loose = dn
      ? all.filter((v) => {
          const vn = String(v.VendorName || '').toLowerCase();
          return vn && (vn.includes(dn) || dn.includes(vn) || vn.split(/\s+/).some((w: string) => w.length > 2 && dn.includes(w)));
        })
      : [];
    const pool = (loose.length >= 1 ? loose : all).slice(0, 30);
    return pool.map((v) => ({
      id: String(v.VendorCode),
      label: String(v.VendorCode) + ' — ' + String(v.VendorName || ''),
      fields: [
        { label: 'Vendor Name', value: v.VendorName == null ? '' : String(v.VendorName) },
        { label: 'Tax ID', value: v.TaxId == null ? '' : String(v.TaxId) },
      ],
      raw: v as Record<string, unknown>,
    }));
  })();

  const headerSel = (kind: string, key: string, code?: string) => {
    let opts: { v: string; name: string; t: string }[] = [];
    if (kind === 'customers') {
      const seen = new Set<string>();
      opts = masters.customers
        .filter((c) => String(c.SalesOrg) === companyCode)
        .map((c) => {
          const v = String(c.ComcompyCodeSAP ?? '').trim();
          const displayName = String(c.CompanyName ?? c.CompanyNameSAP ?? '').trim();
          const searchable = [displayName, c.CompanyNameSAP, c.TaxId, c.Branch, v]
            .map((x) => String(x ?? '').trim()).filter(Boolean).join(' ');
          return { v, name: searchable, t: displayName ? `${v} — ${displayName}` : v };
        })
        // A row without a master key cannot be selected. Duplicate keys would also make React and
        // manual mapping pick an arbitrary row, so expose each usable customer code only once.
        .filter((o) => !!o.v && !seen.has(o.v) && !!seen.add(o.v));
    } else if (kind === 'vendors')
      opts = masters.vendors.map((c) => ({ v: String(c.VendorCode ?? '').trim(), name: String(c.VendorName ?? ''), t: `${c.VendorCode ?? ''} — ${c.VendorName ?? ''}` })).filter((o) => !!o.v);
    else if (kind === 'shiptos') {
      const cc = map.header.customer?.code || '';
      const seen = new Set<string>();
      opts = masters.shiptos
        .filter((s) => String(s.SalesOrg) === companyCode && (!cc || s.CustomerCode === cc))
        .map((s) => {
          const v = String(s.SapShipToCode ?? '').trim();
          const displayName = String(s.ShipToName ?? '').trim();
          const searchable = [displayName, s.Address, s.City, s.PostCode, s.CustomerCode, v]
            .map((x) => String(x ?? '').trim()).filter(Boolean).join(' ');
          return { v, name: searchable, t: displayName ? `${v} — ${displayName}` : v };
        })
        .filter((o) => !!o.v && !seen.has(o.v) && !!seen.add(o.v));
    }
    return (
      <MaterialSearchSelect
        options={opts.map((o) => ({ value: o.v, description: o.name, label: o.t, source: 'local' as const }))}
        value={code || ''}
        disabled={posted}
        emptyLabel="-- Select master mapping --"
        searchPlaceholder="พิมพ์ชื่อหรือรหัสเพื่อค้นหา…"
        ariaLabel={`ค้นหาและเลือก ${kind}`}
        minWidth={260}
        onChange={(value) => onManualHeader(key, value)}
      />
    );
  };

  const addBtn = (label: string, fn: () => void) =>
    posted ? null : (
      <button className="btn sm" style={{ marginLeft: 6 }} onClick={fn}>
        + {label}
      </button>
    );

  // Search this customer's own CustomerMaterial mappings first, but fall back to every other
  // customer's mappings too (same Sales Org) rather than locking the dropdown to just this one
  // CustomerCode -- a material already mapped for someone else is still a useful hit to reuse,
  // and an empty result for a brand-new customer was the whole reason search "found nothing".
  const currentCustomerCode = map.header.customer?.code;
  const availableCustomerMaterials = masters.custmaterials
    .filter((cm) => String(cm.SalesOrg) === companyCode)
    .sort((a, b) => {
      const aMine = a.CustomerCode === currentCustomerCode ? 0 : 1;
      const bMine = b.CustomerCode === currentCustomerCode ? 0 : 1;
      if (aMine !== bMine) return aMine - bMine;
      return String(a.MaterialCodeName || '').localeCompare(String(b.MaterialCodeName || ''));
    });
  const matOpts = doc.module === 'SO'
    ? availableCustomerMaterials.map((m) => ({
        value: m.MaterialCodeSAP,
        description: m.MaterialCodeName || '',
        searchText: `${m.MaterialCodeCode ?? ''} ${m.MaterialCodeName ?? ''}`,
        // Always show the Customer Code, own customer included -- not just for a cross-customer
        // "borrow", so the code is visible (and searchable) for every row, not a special case.
        label: m.CustomerCode === currentCustomerCode
          ? `${m.MaterialCodeSAP} — ${m.MaterialCodeName || ''} (Customer · ${m.CustomerCode})`
          : `${m.MaterialCodeSAP} — ${m.MaterialCodeName || ''} (Customer · ${m.CustomerCode}, other customer)`,
        source: 'local' as const,
      }))
    : (masters.apmaterials || []).map((m) => ({
        value: m.MaterialCode,
        description: m.Description || '',
        label: m.MaterialCode + ' — ' + m.Description,
        source: 'local' as const,
      }));

  const headerCards: React.ReactNode[] = [];
  let n = 0;
  if (doc.module !== 'SO') {
    const r = map.header.vendor;
    n++;
    headerCards.push(
      <CmpCard
        key="vendor"
        no={n}
        title="Vendor / Supplier"
        r={r}
        picker={
          <>
            {headerSel('vendors', 'vendor', r.code)}
            <button className="btn sm" onClick={() => setVendorAiOpen(true)}>
              <i className="fa-solid fa-wand-magic-sparkles" /> Ask AI to match
            </button>
            {r.status === 'fail' && addBtn('Add New Vendor', onQuickAddVendor)}
          </>
        }
      />,
    );
  } else {
    const c = map.header.customer;
    const sh = map.header.shipTo;
    n++;
    headerCards.push(
      <CmpCard
        key="customer"
        no={n}
        title="Customer"
        r={c}
        picker={
          <>
            {headerSel('customers', 'customer', c.code)}
            {c.status === 'fail' && addBtn('Add New Customer', onQuickAddCustomer)}
            {!posted && (
              <button
                className={'btn sm' + (customerSapOpen ? ' primary' : '')}
                style={{ marginLeft: 6 }}
                onClick={() => setCustomerSapOpen((v) => !v)}
                title={isMgt ? 'ค้นหาบัญชีใหม่จาก Zoho CRM' : 'ค้นหาลูกค้าใหม่จาก SAP'}
              >
                <i className="fa-solid fa-magnifying-glass" /> {isMgt ? 'ค้นหาจาก Zoho' : 'ค้นหาจาก SAP'}
              </button>
            )}
          </>
        }
        sourceLabel={isMgt ? 'Data from Zoho CRM' : undefined}
        sapSlot={
          customerSapOpen ? (
            isMgt ? (
              <ZohoCustomerPanel
                customerName={doc.header.customerName}
                taxId={doc.header.customerTaxId}
                address={doc.header.customerAddress}
                disabled={posted}
                onUse={(account) => {
                  onUseZohoAccount(account);
                  setCustomerSapOpen(false);
                }}
                onProposeMatch={onProposeMatch}
                onClearMatch={onClearMatch}
              />
            ) : (
              <SapCustomerPanel
                customerName={doc.header.customerName}
                taxId={doc.header.customerTaxId}
                address={doc.header.customerAddress}
                companyCode={companyCode}
                disabled={posted}
                onUse={(customer) => {
                  onUseSapCustomer(customer);
                  setCustomerSapOpen(false);
                }}
                onProposeMatch={onProposeMatch}
                onClearMatch={onClearMatch}
              />
            )
          ) : isMgt ? (
            // "Sales Org / Channel / Div" is an SAP-only concept (always "-/-/-" for a Zoho
            // account) and "Payment Terms"/"Currency" now have their own live, correctly
            // populated section below (n.3 Credit & Payment Terms) -- showing them here too,
            // usually blank, was confusing duplication. Name + Tax Registration No (the actual
            // identity-match fields) stay.
            <SideList
              items={(c.sap || []).filter((f) => !['Sales Org / Channel / Div', 'Payment Terms', 'Currency'].includes(f.label))}
              side="sap"
            />
          ) : (
            // GLC (SAP): same SAP field list, but the "Payment Terms" row is filled live from the
            // SAP customer master (mapped code -> text), inline with the rest of the customer data.
            <SapCustomerSapSide
              r={c}
              salesOrg={doc.header.salesOrg || undefined}
              companyCode={companyCode || undefined}
              masters={masters}
              header={doc.header}
              posted={posted}
              onSetSalesArea={onSetSalesArea}
            />
          )
        }
      >
        {/* Ship-to lives inside the Customer card as a sub-section instead of its own numbered
            section -- it's meaningless without a matched customer anyway, so keeping the two
            together reads more like "one company record" than two unrelated steps. */}
        <div className={'cmp-sub ' + (sh.status === 'fail' || sh.status === 'needchoice' ? 'bad' : '')}>
          <div className="cmp-head">
            <span className="cmp-no sub">{`${n}.1`}</span>
            <b>Ship-to</b>
            <StatusChip st={sh.status || 'idle'} />
            <span className="hint">{sh.method || ''}</span>
            <div className="sp" />
            {headerSel('shiptos', 'shipTo', sh.code)}
            {sh.status === 'fail' &&
              (c.code ? (
                addBtn('Add New Ship-to', onQuickAddShipTo)
              ) : (
                <span className="hint" style={{ marginLeft: 6 }}>
                  A customer must be specified first
                </span>
              ))}
            {/* GLC: ship-to is optional but the choice must be explicit -- when nothing is matched
                the person picks a real ship-to (select/search above) OR one of these two "no ship-to"
                fallbacks. Both send the same payload (no SH -> SAP uses sold-to); until one is chosen
                the mapping blocks Send ("needchoice"). */}
            {!posted && !isMgt && c.code && sh.status === 'needchoice' && (
              <>
                <button
                  className="btn sm"
                  style={{ marginLeft: 6 }}
                  onClick={() => onManualHeader('shipToFallback', 'soldto')}
                  title="ส่งโดยไม่ใส่ Ship-to — SAP จะใช้ Sold-to เป็นผู้รับให้"
                >
                  ใช้ Sold-to เป็นผู้รับ
                </button>
                <button
                  className="btn sm"
                  style={{ marginLeft: 6 }}
                  onClick={() => onManualHeader('shipToFallback', 'omit')}
                  title="ไม่ส่งค่า Ship-to ไป SAP (SAP จะเติม Sold-to ให้อัตโนมัติ)"
                >
                  ไม่ระบุ Ship-to
                </button>
              </>
            )}
            {!posted && !isMgt && c.code && sh.status === 'skip' && (
              <button
                className="btn sm"
                style={{ marginLeft: 6 }}
                onClick={() => onManualHeader('shipToFallback', '')}
                title="ยกเลิกตัวเลือกนี้ แล้วเลือก Ship-to ใหม่"
              >
                เปลี่ยน
              </button>
            )}
            {!posted && c.code && (isMgt ? onUseZohoShipTo : onUseSapShipTo) && (
              <button
                className={'btn sm' + (shipToSapOpen ? ' primary' : '')}
                style={{ marginLeft: 6 }}
                onClick={() => setShipToSapOpen((v) => !v)}
                title={isMgt ? 'ค้นหาที่อยู่จัดส่งใหม่จาก Zoho CRM' : 'ค้นหาที่อยู่จัดส่งใหม่จาก SAP'}
              >
                <i className="fa-solid fa-magnifying-glass" /> {isMgt ? 'ค้นหาจาก Zoho' : 'ค้นหาจาก SAP'}
              </button>
            )}
          </div>
          <div className="cmp-body">
            <div className="cmp-col">
              <SideList items={sh.doc} side="doc" />
            </div>
            <div className="cmp-arrow">→</div>
            <div className="cmp-col sap">
              {shipToSapOpen && isMgt && onUseZohoShipTo ? (
                <ZohoShipToPanel
                  customerCode={c.code}
                  masters={masters}
                  disabled={posted}
                  onUse={(info, accountId) => {
                    onUseZohoShipTo(info, accountId);
                    setShipToSapOpen(false);
                  }}
                  docShipToName={doc.header.shipToName}
                  docShipToAddress={doc.header.shipToAddress}
                />
              ) : shipToSapOpen && !isMgt && onUseSapShipTo ? (
                <SapShipToPanel
                  soldToSapCode={c.sapCode}
                  docShipToName={doc.header.shipToName}
                  salesOrganization={doc.header.salesOrg || ''}
                  disabled={posted}
                  onUse={(shipTo) => {
                    onUseSapShipTo(shipTo);
                    setShipToSapOpen(false);
                  }}
                />
              ) : (
                <SideList items={sh.sap} side="sap" />
              )}
            </div>
          </div>
        </div>
        {/* Ship-to (n.1, above) and Sold-to (n.2) are the two addresses on this Account -- kept
            immediately adjacent so they read as one "addresses" group rather than two unrelated
            items. */}
        {isMgt && c.code && (
          <div className="cmp-sub">
            <div className="cmp-head">
              <span className="cmp-no sub">{`${n}.2`}</span>
              <b>Sold-to Address</b>
              <div className="sp" />
            </div>
            <div className="cmp-body">
              <div className="cmp-col">
                {doc.header.customerAddress || <span className="hint">—</span>}
              </div>
              <div className="cmp-arrow">→</div>
              <div className="cmp-col sap">
                <ZohoSoldToAddressPanel customerCode={c.code} masters={masters} />
              </div>
            </div>
          </div>
        )}
        {/* Credit & Payment Terms (n.3) stays grouped with the rest of "info about this
            customer record" (name/tax/addresses) -- the Deal comparison is a different kind of
            thing (a next step toward building a Sales Order, not more record data to verify), so
            it's its own top-level card below (see ZohoDealCard) rather than a sub-section here. */}
        {isMgt && c.code && (
          <div className="cmp-sub">
            <div className="cmp-head">
              <span className="cmp-no sub">{`${n}.3`}</span>
              <b>Credit & Payment Terms (Zoho)</b>
              <div className="sp" />
            </div>
            <div className="cmp-body">
              <div className="cmp-col" style={{ flex: '1 1 100%' }}>
                <ZohoCreditInfoPanel customerCode={c.code} masters={masters} />
              </div>
            </div>
          </div>
        )}
      </CmpCard>,
    );
    // Deal comparison is now its own top-level card (see ZohoDealCard) -- promoted out of this
    // sub-section so the person can line up Deal Items against the document's own lines field by
    // field, instead of reading a flat, unrelated list tucked inside the Customer record.
    if (isMgt && c.code) {
      n++;
      headerCards.push(
        <ZohoDealCard
          key="deal"
          no={n}
          customerCode={c.code}
          doc={doc}
          map={map}
          selectedId={selectedDealId || ''}
          onSelectId={onSelectDeal || (() => {})}
          onDealResolved={onDealResolved}
        />,
      );
    }
  }

  const nMat = ++n;
  // Per-line Material cards. SAP path: search the SAP material master. MGT/Zoho path: pick from the
  // matched Deal's Ordered Items (there is no global Zoho material search) and save the pick as a
  // CustomerMaterial mapping — the same persistent mapping the SAP path produces. The SAP-only Unit
  // Conversion sub-section is hidden for MGT (an MGT Sales Order goes to Zoho, not SAP).
  const matCards = doc.lines.map((l, i) => {
    const r = map.lines[i];
    const u = r.unit || { doc: [], sap: [], status: 'idle' };

    return (
      <CmpCard
        key={i}
        no={`${nMat}.${i + 1}`}
        title={`Material — Row ${i + 1}`}
        r={r}
        sourceLabel={isMgt ? 'Data from Zoho CRM (Deal)' : undefined}
        picker={
          <>
            {/* Type to search the CustomerMaterial options by description/code and pick a
                suggestion to select it (one combobox, no separate search box + plain <select>).
                The live SAP/Zoho search + compare still lives in the right-hand column
                (SapMaterialPanel / ZohoMaterialPanel in sapSlot below), same as the Customer card.
                SAP path: route the pick through onUseSapMaterial (same handler SapMaterialPanel's
                own "Use" button calls) instead of a bare manual-code set, so picking here -- an
                already-mapped code for this customer, or one borrowed from another customer's
                CustomerMaterial row via the cross-customer search -- gets the same treatment: a
                CustomerMaterial row is ensured for THIS customer and the SAP unit is checked/
                confirmed, so "DATA FROM SAP" fills in the same as picking straight from SAP.
                Clearing the field (no option) and the MGT/Zoho path both keep the plain set. */}
            <MaterialSearchSelect
              options={matOpts}
              value={r.code || ''}
              disabled={posted}
              emptyLabel="-- Select master mapping --"
              onChange={(value, option) => {
                if (!isMgt && option) {
                  onUseSapMaterial(i, { materialCode: option.value, materialDescription: option.description });
                } else {
                  onManualLine(i, value);
                }
              }}
            />
            {r.status === 'fail' && addBtn('Add New Material', () => onQuickAddMaterial(i))}
            {!posted && (
              <button
                className={'btn sm' + (matSapOpen[i] ? ' primary' : '')}
                style={{ marginLeft: 6 }}
                onClick={() => setMatSapOpen((prev) => ({ ...prev, [i]: !prev[i] }))}
                title={isMgt ? 'ค้นหา Material ใหม่จาก Zoho (Deal)' : 'ค้นหา Material ใหม่จาก SAP'}
              >
                <i className="fa-solid fa-magnifying-glass" /> {isMgt ? 'ค้นหาจาก Zoho' : 'ค้นหาจาก SAP'}
              </button>
            )}
          </>
        }
        sapSlot={
          !matSapOpen[i] ? undefined
            : isMgt ? (
              <ZohoMaterialPanel
                dealItems={dealItems || []}
                docDescription={l.desc}
                docCode={l.extCode}
                disabled={posted}
                onUse={(it) => {
                  onUseZohoMaterial?.(i, it);
                  setMatSapOpen((prev) => ({ ...prev, [i]: false }));
                }}
              />
            ) : (
              <SapMaterialPanel
                docDescription={l.desc}
                docCode={l.extCode}
                plant={plant}
                customer={map.header.customer?.sapCode || map.header.customer?.code}
                disabled={posted}
                onUse={(m) => {
                  onUseSapMaterial(i, m);
                  setMatSapOpen((prev) => ({ ...prev, [i]: false }));
                }}
              />
            )
        }
      >
        {!isMgt && r.code && (
          <div className={'cmp-sub ' + (u.status === 'fail' ? 'bad' : '')}>
            <div className="cmp-head">
              <span className="cmp-no sub">{nMat + 1}</span>
              <b>Relate Unit — Unit Conversion</b>
              <StatusChip st={u.status === 'fail' ? 'unitfail' : u.status} />
              <div className="sp" />
              {u.status === 'fail' && !posted && onFetchSapUom && (
                <button className="btn sm" onClick={() => onFetchSapUom(i)}>
                  ดึงหน่วยแปลงจาก SAP
                </button>
              )}
              {u.status === 'fail' && !posted && (
                <button className="btn sm" onClick={() => onAddUomRule(i)}>
                  + Add Unit Conversion Rule
                </button>
              )}
            </div>
            <div className="cmp-body">
              <div className="cmp-col">
                <SideList items={u.doc} side="doc" />
              </div>
              <div className="cmp-arrow">→</div>
              <div className="cmp-col sap">
                <SideList items={u.sap} side="sap" />
              </div>
            </div>
          </div>
        )}
        {/* GLC only, per matched line: which sales person handled/verified this line's mapping.
            Auto-filled from the Sales Order history of this customer+material, editable from the full
            list. Persisted per line and sent as the SAP item custom field YY1_SDSalesEmployeeI_SDI. */}
        {!isMgt && r.code && onSetLineSalesEmployee && (
          <SalesEmployeePicker
            value={((l.extra as Record<string, string> | undefined)?.salesEmployee) || ''}
            suggestion={lineSalesEmpSuggest?.[i]}
            list={salesEmployees || []}
            disabled={posted}
            onChange={(personId) => onSetLineSalesEmployee(i, personId)}
          />
        )}
      </CmpCard>
    );
  });

  return (
    <div className="card">
      <div className="card-h">
        <h2>ผลการจับคู่ข้อมูลกับ {isMgt ? 'Zoho CRM' : 'SAP'}</h2>
        <div className="sp" />
        {/* The pass/fail badge reflects the SAP material-master check, which an MGT/Zoho document
            doesn't use -- its material matching lives in the Sales Order editor below -- so it's
            shown for the SAP path only. */}
        {!isMgt &&
          (map.pass ? (
            <span className="badge b-ok"><i className="fa-solid fa-check" /> All matched</span>
          ) : (
            <span className="badge b-fail"><i className="fa-solid fa-xmark" /> ไม่พบข้อมูล {map.errors.length} รายการ</span>
          ))}
      </div>
      <div className="card-b">
        {headerCards}
        {/* Material/UoM mapping belongs to Sales Orders and PO-based Supplier Invoices. An
            Incoming Invoice (II/FB60) posts G/L account items instead, so OCR lines there must not
            be presented as Sales-Order-style materials. */}
        {(doc.module === 'SO' || doc.module === 'AP') && <>
          <p className="sec-title" style={{ marginTop: 20 }}>
            {isMgt
              ? `${nMat}. MATERIAL (per line)`
              : `${nMat}. MATERIAL & ${nMat + 1}. RELATE UNIT (per line)`}
          </p>
          {matCards}
        </>}
        {detailContent && (
          <>
            <p className="sec-title" style={{ marginTop: 20 }}>
              DETAIL — Line Items (from the document)
            </p>
            {detailContent}
          </>
        )}
      </div>
      {doc.module !== 'SO' && (
        <CompareModal<Record<string, unknown>>
          open={vendorAiOpen}
          onClose={() => setVendorAiOpen(false)}
          title="AI Suggested Match — Vendor / Supplier"
          docFields={[
            { label: 'Vendor Name', value: doc.header.vendorName },
            { label: 'Tax ID', value: doc.header.vendorTaxId },
          ]}
          candidates={vendorAiCandidates}
          providers={ocrProviders || []}
          onUse={(v) => {
            onManualHeader('vendor', String(v.VendorCode));
            setVendorAiOpen(false);
          }}
        />
      )}
    </div>
  );
}

export { qtyTxt };
