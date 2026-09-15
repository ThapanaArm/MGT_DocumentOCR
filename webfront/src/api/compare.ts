import { api } from './client';

/* AI-assisted candidate comparison — POST /api/compare (MgtOcr.Ocr.CompareAdvisor).
   Stateless: send the document's own fields plus one or more external-system candidates
   (SAP Business Partner, Zoho CRM Account, ...), get back a verdict per candidate, and
   optionally (when `instruction` — the person's chat message — is given) a `decision` of what
   the AI thinks should happen next. Used by DocumentPage's chat-driven customer-matching flow.
   This call itself never selects or saves anything — the frontend is what applies a match, and
   only when decision.action is "select". */

export interface CompareField {
  label: string;
  value?: string | null;
}

export interface CompareCandidate {
  id: string;
  label: string;
  fields: CompareField[];
}

export type CompareMatch = 'yes' | 'no' | 'maybe';

export interface CompareVerdict {
  candidateId: string;
  match: CompareMatch;
  confidence: number;
  reason: string;
}

export type CompareAction = 'select' | 'suggest' | 'none';

export interface CompareDecision {
  candidateId: string | null;
  action: CompareAction;
  reason: string;
}

export interface CompareResult {
  verdicts: CompareVerdict[];
  /** Only meaningful when `instruction` was sent — see CompareAction above. */
  decision?: CompareDecision | null;
}

export const compareCandidates = (
  docFields: CompareField[],
  candidates: CompareCandidate[],
  provider = 'claude',
  instruction?: string,
  priorSuggestedCandidateId?: string,
) =>
  api.post<CompareResult>('/api/compare', {
    docFields,
    candidates,
    provider,
    instruction,
    priorSuggestedCandidateId,
  });

/* AI-assisted search-keyword suggestion — POST /api/compare/search-keywords
   (MgtOcr.Ocr.SearchAdvisor). A person explicitly asks for this (a "search with AI" button) after
   a live search (SAP Business Partner name search today) finds nothing — the AI proposes a few
   alternative search terms, and the caller re-runs its own normal search with whichever one is
   picked. This call never searches anything itself. `kind` is a short label for what's being
   searched for ("customer", "material", "ship-to", ...), used only to steer the AI's prompt. */

export interface SearchKeywordsResult {
  keywords: string[];
  reason: string;
}

export const suggestSearchKeywords = (text: string, kind: string, provider = 'claude') =>
  api.post<SearchKeywordsResult>('/api/compare/search-keywords', { text, kind, provider });
