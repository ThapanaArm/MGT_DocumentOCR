import { useEffect, useState } from 'react';
import Modal, { ModalHeader } from '../Modal';
import OcrProviderSelect from '../OcrProviderSelect';
import type { OcrProvider } from '../../api/masters';
import { compareCandidates, type CompareField, type CompareVerdict } from '../../api/compare';

// Same three "AI" engines Chat to Fix Data offers — reuse the same providers list/ready flags
// so picking a model here doesn't need any separate configuration.
const AI_PROVIDER_IDS = ['claude', 'gemini', 'openai'];

/* Generic "compare N candidates side by side, optionally ask AI" popup. Used by any Mapping
   card's live search (SapCustomerPanel, ZohoCustomerPanel today; Vendor/Ship-to/Material later
   can reuse it the same way) whenever more than one candidate comes back — instead of squeezing
   a per-field comparison into a narrow column, this lays Document | Candidate 1 | Candidate 2...
   out as a proper table, with an "Ask AI to compare" button that fills in a verdict + one-line
   reason per candidate (POST /api/compare). The AI only advises: nothing here saves or picks
   anything on its own — the person still clicks "Use this record" themselves. */

export interface CompareCandidateItem<T> {
  id: string;
  label: string;
  fields: CompareField[];
  /** the original record, handed back to onUse verbatim */
  raw: T;
}

export default function CompareModal<T>({
  open,
  onClose,
  title,
  docFields,
  candidates,
  onUse,
  disabled,
  providers = [],
  onSendToChat,
  fetchFullFields,
  onVerdicts,
}: {
  open: boolean;
  onClose: () => void;
  title: string;
  docFields: CompareField[];
  candidates: CompareCandidateItem<T>[];
  onUse: (raw: T) => void;
  disabled?: boolean;
  /** AI provider list (with ready flags) — the same one Chat to Fix Data uses. */
  providers?: OcrProvider[];
  /** Posts a composed summary of this comparison into the document's Chat to Fix Data box, so
   *  the person can keep discussing it there in free form. Omit to hide the "Send to chat"
   *  shortcut entirely. */
  onSendToChat?: (message: string) => void;
  /** Loads every field the source system has for one candidate (not just the few columns the
   *  caller already searched with) — e.g. Zoho's full Account record. When provided, this is
   *  called once per candidate as soon as the popup opens, and the fuller field set replaces
   *  the caller's curated `fields` for that column as soon as it's back, so the comparison
   *  (and the AI) isn't limited to just the fields the document itself carries. */
  fetchFullFields?: (candidate: CompareCandidateItem<T>) => Promise<CompareField[]>;
  /** Reports the AI verdicts (keyed by candidate id) back to the caller each time "Ask AI"
   *  succeeds, so the calling panel can keep showing the match/confidence numbers after this
   *  popup is closed. */
  onVerdicts?: (verdicts: Record<string, CompareVerdict>) => void;
}) {
  const [verdicts, setVerdicts] = useState<Record<string, CompareVerdict>>({});
  const [asking, setAsking] = useState(false);
  const [askError, setAskError] = useState<string | null>(null);
  const [asked, setAsked] = useState(false);
  const aiModels = providers.filter((p) => AI_PROVIDER_IDS.includes(p.id));
  const [provider, setProvider] = useState('claude');
  const [fullFields, setFullFields] = useState<Record<string, CompareField[]>>({});
  const [loadingFull, setLoadingFull] = useState(false);

  // As soon as the popup opens, pull the full record for every candidate (if the caller wired
  // it up) so the table isn't stuck showing only the handful of fields the search itself used.
  useEffect(() => {
    if (!open || !fetchFullFields) return;
    let cancelled = false;
    setLoadingFull(true);
    Promise.all(
      candidates.map((c) =>
        fetchFullFields(c)
          .then((fields) => [c.id, fields] as const)
          .catch(() => [c.id, null] as const),
      ),
    ).then((pairs) => {
      if (cancelled) return;
      setFullFields((prev) => {
        const next = { ...prev };
        for (const [id, fields] of pairs) if (fields) next[id] = fields;
        return next;
      });
      setLoadingFull(false);
    });
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, candidates.map((c) => c.id).join(',')]);

  // The full record once loaded, otherwise the curated fields the caller searched with.
  const effectiveFields = (c: CompareCandidateItem<T>) => fullFields[c.id] ?? c.fields;

  const askAi = () => {
    setAsking(true);
    setAskError(null);
    compareCandidates(
      docFields,
      candidates.map((c) => ({ id: c.id, label: c.label, fields: effectiveFields(c) })),
      provider,
    )
      .then((r) => {
        const map: Record<string, CompareVerdict> = {};
        for (const v of r.verdicts || []) map[v.candidateId] = v;
        setVerdicts(map);
        setAsked(true);
        onVerdicts?.(map);
      })
      .catch((e) => setAskError(e?.message || 'AI comparison failed'))
      .finally(() => setAsking(false));
  };

  // Union of field labels across the document + every candidate, in first-seen order, so a
  // field one candidate lacks still lines up as a blank row rather than shifting the table.
  const labels: string[] = [];
  const seen = new Set<string>();
  for (const f of [...docFields, ...candidates.flatMap((c) => effectiveFields(c))]) {
    if (!seen.has(f.label)) {
      seen.add(f.label);
      labels.push(f.label);
    }
  }
  const valueFor = (fields: CompareField[], label: string) => fields.find((f) => f.label === label)?.value || '';

  const buildChatMessage = () => {
    const lines: string[] = [
      `${title} — please compare these ${candidates.length} candidates against this document and tell me which one is correct.`,
      '',
      'Document:',
    ];
    for (const f of docFields) if (f.value) lines.push(`  ${f.label}: ${f.value}`);
    lines.push('');
    candidates.forEach((c, i) => {
      lines.push(`${i + 1}. ${c.label}`);
      for (const f of effectiveFields(c)) if (f.value) lines.push(`   ${f.label}: ${f.value}`);
      const v = verdicts[c.id];
      if (asked && v) lines.push(`   AI verdict so far: ${v.match} (${v.confidence}%) — ${v.reason}`);
    });
    return lines.join('\n');
  };

  const sendToChat = () => {
    onSendToChat?.(buildChatMessage());
    onClose();
  };

  const matchBadge = (v?: CompareVerdict) => {
    if (!v) return <span className="hint">—</span>;
    const cls = v.match === 'yes' ? 'b-ok' : v.match === 'no' ? 'b-fail' : 'b-warn';
    const icon = v.match === 'yes' ? 'fa-check' : v.match === 'no' ? 'fa-xmark' : 'fa-question';
    const text = v.match === 'yes' ? 'Match' : v.match === 'no' ? 'Not a match' : 'Maybe';
    return (
      <span className={'badge ' + cls}>
        <i className={'fa-solid ' + icon} /> {text} ({v.confidence}%)
      </span>
    );
  };

  return (
    <Modal open={open} onClose={onClose} wide>
      <ModalHeader title={title} onClose={onClose} />
      <div className="card-b">
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 12, flexWrap: 'wrap' }}>
          <p className="hint" style={{ margin: 0, flex: 1 }}>
            {candidates.length} possible matches found — compare the fields below, or ask AI for a verdict on each.
          </p>
          {aiModels.length > 0 && (
            <OcrProviderSelect providers={aiModels} value={provider} onChange={setProvider} className="ocr-pick" />
          )}
          <button className="btn sm primary" onClick={askAi} disabled={asking}>
            <i className="fa-solid fa-wand-magic-sparkles" /> {asking ? 'Asking AI…' : asked ? 'Ask AI again' : 'Ask AI to compare'}
          </button>
          {onSendToChat && (
            <button
              className="btn sm ghost"
              onClick={sendToChat}
              title="Send this comparison to the Chat to Fix Data box below and keep discussing it there"
            >
              <i className="fa-solid fa-comment-dots" /> Send to chat
            </button>
          )}
        </div>
        {loadingFull && <p className="hint" style={{ margin: '0 0 10px' }}>Loading the full record for each match…</p>}
        {askError && <p className="hint" style={{ margin: '0 0 10px' }}>Could not get an AI comparison: {askError}</p>}
        {aiModels.length > 0 && !aiModels.some((p) => p.ready) && (
          <p className="hint" style={{ margin: '0 0 10px' }}>
            You must configure at least one AI model API key before "Ask AI to compare" will work
          </p>
        )}
        <div className="tw" style={{ overflowX: 'auto' }}>
          <table className="cmp">
            <thead>
              <tr>
                <th></th>
                <th>Document</th>
                {candidates.map((c) => (
                  <th key={c.id}>{c.label}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {labels.map((label) => (
                <tr key={label}>
                  <th>{label}</th>
                  <td>{valueFor(docFields, label) || <span className="hint">—</span>}</td>
                  {candidates.map((c) => (
                    <td key={c.id}>{valueFor(effectiveFields(c), label) || <span className="hint">—</span>}</td>
                  ))}
                </tr>
              ))}
              <tr>
                <th>AI verdict</th>
                <td></td>
                {candidates.map((c) => (
                  <td key={c.id}>{matchBadge(verdicts[c.id])}</td>
                ))}
              </tr>
              {asked && (
                <tr>
                  <th>Reason</th>
                  <td></td>
                  {candidates.map((c) => (
                    <td key={c.id} className="hint">
                      {verdicts[c.id]?.reason || '—'}
                    </td>
                  ))}
                </tr>
              )}
              <tr>
                <th></th>
                <td></td>
                {candidates.map((c) => (
                  <td key={c.id}>
                    <button className="btn sm primary" disabled={disabled} onClick={() => onUse(c.raw)}>
                      Use this record
                    </button>
                  </td>
                ))}
              </tr>
            </tbody>
          </table>
        </div>
      </div>
    </Modal>
  );
}
