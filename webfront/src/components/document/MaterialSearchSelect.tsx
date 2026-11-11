import { useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';

export interface MaterialSearchOption {
  value: string;
  description: string;
  label: string;
  /** Explicit fields used for matching when display-only text must not affect the results. */
  searchText?: string;
  source?: 'local' | 'sap' | 'deal' | 'zoho';
  /** Heading the option is listed under (fuzzy mode); options keep their group order. */
  group?: string;
  /** Small second line under the label (unit conversion, material group…). */
  hint?: string;
  /** Caller's own record behind the option (e.g. the Zoho Deal item), handed back in onChange. */
  data?: unknown;
}

/* Copy & paste search (5 Nov 2026). A whole PO line pasted into the box -- "HPC - L (5 x 10 Kg.)
   กิโลกรัม(Nippon Soda / Japan)" -- is never a substring of a Material name, so in `fuzzy` mode an
   option matches on the share of meaningful words it has in common (pack sizes / units ignored), or
   when its code appears inside the pasted text, and the list is ranked closest first. */
const NOISE_WORDS = new Set([
  'kg', 'kgs', 'kilogram', 'kilograms', 'g', 'gm', 'ltr', 'liter', 'litre', 'lt', 'ml', 'ton', 'tons', 'mt',
  'bag', 'bags', 'bg', 'drum', 'drums', 'dr', 'pail', 'pl', 'can', 'cn', 'box', 'bx', 'pc', 'pcs', 'ea', 'set',
  'ibc', 'tote', 'carton', 'ctn', 'pallet', 'loose', 'lo', 'std', 'the', 'and', 'for', 'with', 'per', 'x',
  'กิโลกรัม', 'กก', 'ถุง', 'ถัง', 'ลิตร', 'กล่อง', 'ชิ้น', 'ตัน',
]);
const searchWords = (s: string) =>
  (String(s ?? '').toLocaleLowerCase().match(/[a-z0-9\u0E00-\u0E7F]+/gi) || [])
    .filter((w) => w.length >= 2 && !NOISE_WORDS.has(w) && !/^\d+$/.test(w));
const compactText = (s: string) => String(s ?? '').toLocaleLowerCase().replace(/[^a-z0-9\u0E00-\u0E7F]/gi, '');

/** 0 = no match. Higher = closer. Exported so cards can rank their own lists the same way. */
export function pasteMatchScore(query: string, text: string, code?: string): number {
  const q = compactText(query);
  if (!q) return 0;
  const t = compactText(text);
  const c = compactText(code || '');
  let score = 0;
  if (c && c === q) score += 1000;
  else if (c.length >= 4 && q.includes(c)) score += 300; // the pasted line carries this code
  else if (c && c.includes(q)) score += 250; // part of the code typed
  if (t && t.includes(q)) score += 200;
  const qw = [...new Set(searchWords(query))];
  const tw = new Set(searchWords(`${code ?? ''} ${text}`));
  if (qw.length && tw.size) {
    const hit = qw.filter((w) => tw.has(w) || (w.length >= 4 && t.includes(w))).length;
    // One shared word out of a long pasted line is too weak on its own.
    if (hit >= 2 || hit / qw.length >= 0.5) score += (60 * hit) / qw.length + (40 * hit) / tw.size;
  }
  return score;
}

/* A searchable dropdown (custom combobox) for picking a Material.

   Not a native <input list> + <datalist>: the browser filters a datalist by the input's own text,
   and the box shows the selected option's full label, so opening it filtered every option against
   that whole label and the list collapsed to just the current item (or wouldn't open). This is a
   real click-to-open panel we render ourselves instead.

   The panel is position:fixed AND rendered through a portal into document.body. Both matter:
   the DETAIL table sits inside a `.tw { overflow:auto }` wrapper and the cards sit inside
   `.card { overflow:hidden }` / `.content { overflow-x:auto }`, and any ancestor that establishes
   a containing block (a `transform`, `filter`, `will-change`, hover/animation transform, or the
   `body.busy .app { filter }` state) would trap a plain fixed panel — it would then behave like an
   absolutely-positioned box inside that ancestor, land far off from the input, get clipped so only
   a sliver (the summary footer) shows, and add to that ancestor's scroll height (the "weird scroll").
   Portaling to document.body removes every such ancestor, so the fixed panel truly anchors to the
   viewport. reposition() keeps it aligned to the input and flips it above / clamps it to the screen
   so the list is always fully visible and clickable, in the table and in the Mapping cards alike. */

type PanelRect = {
  left: number;
  width: number;
  top?: number;
  bottom?: number;
  maxHeight: number;
};

const PANEL_MAX_HEIGHT = 380;
const VIEWPORT_MARGIN = 8;
// Master data returned by the API can contain null even when the TypeScript contract says string.
// Normalize only for comparison so a single incomplete row cannot crash the whole document page.
const normalizeSearchText = (value: unknown) => String(value ?? '').toLocaleLowerCase();
// The panel is at least this wide even when the input itself is narrow (e.g. squeezed into a
// table cell) -- labels now always carry the Customer Code too ("CODE — Name (CustomerMaterial ·
// 0010001)"), which wraps to 2-3 cramped lines at the old width. Widening only the dropdown (not
// the input box, which still has to fit its table cell/card layout) fixes that without touching
// surrounding layout.
const PANEL_MIN_WIDTH = 420;

export default function MaterialSearchSelect({
  options,
  value,
  disabled,
  onChange,
  searchSap,
  emptyLabel = '-- Not found / Please select --',
  searchPlaceholder = 'Type a name or code to search…',
  ariaLabel = 'Search and select an item',
  minWidth = 300,
  fuzzy = false,
  remoteLabel = 'SAP',
  currentLabel,
  query: queryProp,
  onQueryChange,
  noLocalFilter = false,
  loading = false,
  renderExtra,
  onSearch,
  idleText,
}: {
  options: MaterialSearchOption[];
  value: string;
  disabled?: boolean;
  onChange: (value: string, option?: MaterialSearchOption) => void;
  searchSap?: (keyword: string) => Promise<MaterialSearchOption[]>;
  emptyLabel?: string;
  searchPlaceholder?: string;
  ariaLabel?: string;
  minWidth?: number;
  /** Rank by words in common instead of a plain substring test -- for pasting a whole PO line. */
  fuzzy?: boolean;
  /** Name of the system `searchSap` really searches (badge + footer), e.g. "Zoho". */
  remoteLabel?: string;
  /** Shown for a `value` that none of the options carries (e.g. picked for this document only). */
  currentLabel?: string;
  /** Controlled search text (search-box mode): the box always shows it and it is kept when the list
   *  opens / closes -- the caller runs its own search on it and passes the hits as `options`. */
  query?: string;
  onQueryChange?: (q: string) => void;
  /** The options are already the caller's ranked search hits -- list them as they are. */
  noLocalFilter?: boolean;
  /** The caller's own search is running. */
  loading?: boolean;
  /** Extra content under an option's label (e.g. Last Price). */
  renderExtra?: (o: MaterialSearchOption) => React.ReactNode;
  /** Search on demand: shows a Search button next to the box, and Enter runs it instead of picking.
   *  The caller searches its controlled `query` and passes the hits as `options`. */
  onSearch?: () => void;
  /** Shown in the open list when there are no options yet (e.g. before the first search). */
  idleText?: string;
}) {
  const [open, setOpen] = useState(false);
  const [innerQuery, setInnerQuery] = useState(''); // what the person is typing while the panel is open
  const controlled = queryProp !== undefined;
  const query = controlled ? queryProp : innerQuery;
  const setQuery = (v: string) => (controlled ? onQueryChange?.(v) : setInnerQuery(v));
  const [sapOptions, setSapOptions] = useState<MaterialSearchOption[]>([]);
  const [sapLoading, setSapLoading] = useState(false);
  const [sapError, setSapError] = useState('');
  const [hover, setHover] = useState<string | null>(null);
  const [rect, setRect] = useState<PanelRect | null>(null);
  const rootRef = useRef<HTMLDivElement>(null);
  const panelRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLInputElement>(null);

  const findOpt = (v: string) => [...options, ...sapOptions].find((o) => o.value === v);
  const selected = value ? findOpt(value) : undefined;
  const selectedLabel = selected?.label ?? (value ? currentLabel || value : '');

  // Anchor the fixed panel to the input's on-screen box, but keep it inside the viewport: flip it
  // above the input when there isn't room below, cap its height to the space available, and clamp
  // its left edge so a combobox near the right edge of a wide table doesn't push off-screen.
  const reposition = () => {
    const el = inputRef.current;
    if (!el) return;
    const r = el.getBoundingClientRect();
    const vw = window.innerWidth;
    const vh = window.innerHeight;

    const spaceBelow = vh - r.bottom;
    const spaceAbove = r.top;
    const openUp = spaceBelow < 220 && spaceAbove > spaceBelow;
    const available = (openUp ? spaceAbove : spaceBelow) - 12;
    const maxHeight = Math.max(140, Math.min(PANEL_MAX_HEIGHT, available));

    // Never narrower than the input, never narrower than PANEL_MIN_WIDTH, but still capped to the
    // viewport (with margins) so it can't overflow the screen on a small window.
    const width = Math.min(Math.max(r.width, PANEL_MIN_WIDTH), vw - VIEWPORT_MARGIN * 2);
    const left = Math.min(Math.max(VIEWPORT_MARGIN, r.left), Math.max(VIEWPORT_MARGIN, vw - width - VIEWPORT_MARGIN));

    setRect({
      left,
      width,
      maxHeight,
      ...(openUp ? { bottom: vh - r.top + 4 } : { top: r.bottom + 4 }),
    });
  };

  const openPanel = () => {
    if (disabled) return;
    reposition();
    setOpen(true);
    if (!controlled) setQuery('');
  };
  const close = () => {
    setOpen(false);
    if (!controlled) setQuery('');
  };

  // Close on outside click; keep the fixed panel anchored to the input as the page scrolls/resizes.
  // The panel is portaled out of rootRef, so an outside-click check must treat the panel itself as
  // "inside" too (via panelRef) — otherwise clicking the scrollbar/footer would close it.
  useEffect(() => {
    if (!open) return;
    const onDocDown = (e: MouseEvent) => {
      const t = e.target as Node;
      if (rootRef.current?.contains(t)) return;
      if (panelRef.current?.contains(t)) return;
      close();
    };
    const onScroll = () => reposition();
    document.addEventListener('mousedown', onDocDown);
    window.addEventListener('scroll', onScroll, true);
    window.addEventListener('resize', onScroll);
    return () => {
      document.removeEventListener('mousedown', onDocDown);
      window.removeEventListener('scroll', onScroll, true);
      window.removeEventListener('resize', onScroll);
    };
  }, [open]);

  // Live SAP search on the typed query (only while open and enough has been typed).
  useEffect(() => {
    const q = query.trim();
    if (!searchSap || q.length < 2) {
      setSapOptions([]);
      setSapError('');
      return;
    }
    let active = true;
    const timer = window.setTimeout(async () => {
      setSapLoading(true);
      setSapError('');
      try {
        const rows = await searchSap(q);
        if (active) setSapOptions(rows);
      } catch (e) {
        if (active) setSapError(e instanceof Error ? e.message : `${remoteLabel} search failed`);
      } finally {
        if (active) setSapLoading(false);
      }
    }, 400);
    return () => {
      active = false;
      window.clearTimeout(timer);
    };
  }, [query, searchSap]);

  const q = normalizeSearchText(query.trim());
  // Compute from the current keystroke on every render. The options are API-backed mutable rows in
  // several callers, so memoizing this list could retain results from the previous query/data set.
  // Fuzzy: score every option, keep group order, closest first inside each group.
  const groupOrder = (o: MaterialSearchOption) => {
    const gi = options.findIndex((x) => (x.group || '') === (o.group || ''));
    return gi < 0 ? 0 : gi;
  };
  const fuzzyRank = (list: MaterialSearchOption[]) =>
    list
      .map((o, idx) => ({ o, idx, sc: pasteMatchScore(query, o.searchText ?? o.label, o.value) }))
      .filter((x) => x.sc > 0)
      .sort((a, b) => groupOrder(a.o) - groupOrder(b.o) || b.sc - a.sc || a.idx - b.idx)
      .map((x) => x.o);
  const localVisible = !q || noLocalFilter
    ? options
    : fuzzy
    ? fuzzyRank(options)
    : options.filter(
        (o) => o.searchText !== undefined
          ? normalizeSearchText(o.searchText).includes(q)
          : normalizeSearchText(o.value).includes(q) || normalizeSearchText(o.label).includes(q),
      );

  const listOptions = [
    ...localVisible,
    // Fuzzy: the remote search already ranked its hits against the whole text -- keep them as is.
    ...sapOptions.filter(
      (s) =>
        (fuzzy || !q || normalizeSearchText(s.value).includes(q) || normalizeSearchText(s.label).includes(q)) &&
        !localVisible.some((o) => o.value === s.value),
    ),
  ];

  // Keep the panel anchored after its own content (and therefore height) changes while typing.
  useEffect(() => {
    if (open) reposition();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, listOptions.length]);

  const pick = (o: MaterialSearchOption) => {
    if (o.value !== value) onChange(o.value, o);
    close();
  };

  const rowStyle = (active: boolean, isSelected: boolean): React.CSSProperties => ({
    padding: '9px 12px',
    cursor: 'pointer',
    fontSize: 13.5,
    lineHeight: 1.45,
    background: active ? 'var(--brand-soft)' : isSelected ? 'var(--line-soft)' : 'transparent',
    color: 'var(--text)',
    borderRadius: 6,
  });

  const panel = open && rect && (
    <div
      ref={panelRef}
      style={{
        position: 'fixed',
        left: rect.left,
        width: rect.width,
        ...(rect.top !== undefined ? { top: rect.top } : { bottom: rect.bottom }),
        zIndex: 3000,
        background: 'var(--card)',
        border: '1px solid var(--line)',
        borderRadius: 'var(--r3)',
        boxShadow: 'var(--sh3)',
        maxHeight: rect.maxHeight,
        overflowY: 'auto',
        padding: 4,
      }}
    >
      {selectedLabel && (
        <div style={{ padding: '4px 10px 6px', fontSize: 11, color: 'var(--muted)' }}>
          Current: {selectedLabel}
        </div>
      )}
      {!q && value && (
        <div
          style={rowStyle(false, false)}
          onMouseDown={(e) => {
            e.preventDefault();
            onChange('');
            close();
          }}
        >
          {emptyLabel}
        </div>
      )}
      {listOptions.length === 0 && (
        <div style={{ padding: '10px', fontSize: 13, color: 'var(--muted)' }}>
          {sapLoading || loading ? 'Searching…' : idleText || 'No items found — try another search term'}
        </div>
      )}
      {listOptions.map((o, index) => {
        const groupHead = fuzzy && o.group && o.group !== listOptions[index - 1]?.group ? (
          <div style={{ padding: '8px 10px 2px', fontSize: 11, fontWeight: 700, color: 'var(--muted)', textTransform: 'uppercase' }}>
            {o.group}
          </div>
        ) : null;
        // CustomerMaterial legitimately contains several rows that resolve to the same SAP
        // material code (different customer code/name mappings). `value` therefore is not a
        // unique React identity; include the mapping text and final position as a stable tie-break.
        const key = [
          o.source === 'sap' ? 'sap' : 'local',
          normalizeSearchText(o.value),
          normalizeSearchText(o.searchText ?? o.label),
          index,
        ].join(':');
        return (
          <div key={key}>
          {groupHead}
          <div
            style={rowStyle(hover === key, o.value === value)}
            onMouseEnter={() => setHover(key)}
            onMouseLeave={() => setHover(null)}
            onMouseDown={(e) => {
              e.preventDefault(); // fire before the input's blur so the pick isn't lost
              pick(o);
            }}
          >
            {o.label}
            {(o.source === 'sap' || o.source === 'zoho') && (
              <span
                style={{
                  marginLeft: 6,
                  fontSize: 11,
                  color: 'var(--info)',
                  border: '1px solid var(--info)',
                  borderRadius: 4,
                  padding: '0 4px',
                }}
              >
                {o.source === 'zoho' ? 'Zoho' : 'SAP'}
              </span>
            )}
            {o.value === value && (
              <i className="fa-solid fa-check" style={{ marginLeft: 6, color: 'var(--brand)' }} />
            )}
            {o.hint && <div style={{ fontSize: 11.5, color: 'var(--muted)' }}>{o.hint}</div>}
            {renderExtra?.(o)}
          </div>
          </div>
        );
      })}
      {searchSap && q.length >= 2 && (
        <div style={{ padding: '6px 10px', fontSize: 12, color: 'var(--muted)' }}>
          {sapLoading ? `Searching ${remoteLabel}…` : `Found ${sapOptions.length} in ${remoteLabel}`}
          {sapError && ` · ${sapError}`}
        </div>
      )}
      <div
        style={{
          padding: '6px 10px',
          fontSize: 11,
          color: 'var(--muted)',
          borderTop: '1px solid var(--line-soft)',
          position: 'sticky',
          bottom: 0,
          background: 'var(--card)',
        }}
      >
        Showing {listOptions.length} / {options.length} items
        {q ? ` · search "${query.trim().length > 50 ? query.trim().slice(0, 50) + '…' : query.trim()}"` : ''}
      </div>
    </div>
  );

  return (
    <div ref={rootRef} style={{ position: 'relative', minWidth, ...(onSearch ? { display: 'flex', gap: 8, alignItems: 'center' } : {}) }}>
      <div style={{ position: 'relative', ...(onSearch ? { flex: '1 1 auto', minWidth: 0 } : {}) }}>
        <i
          className="fa-solid fa-magnifying-glass"
          style={{
            position: 'absolute',
            left: 11,
            top: '50%',
            transform: 'translateY(-50%)',
            color: 'var(--muted)',
            fontSize: 12,
            pointerEvents: 'none',
          }}
        />
        <input
          ref={inputRef}
          type="text"
          value={open || controlled ? query : selectedLabel}
          title={(open || controlled ? query : selectedLabel) || undefined}
          disabled={disabled}
          placeholder={open ? searchPlaceholder : emptyLabel}
          aria-label={ariaLabel}
          style={{ width: '100%', paddingLeft: 30, paddingRight: 30, cursor: disabled ? 'default' : 'text' }}
          onFocus={openPanel}
          onMouseDown={() => {
            if (!open) openPanel();
          }}
          onChange={(e) => {
            if (!open) setOpen(true);
            setQuery(e.target.value);
          }}
          onPaste={(e) => {
            // A cell copied from Excel / a PDF line can carry tabs and line breaks -- one clean line.
            const text = e.clipboardData?.getData('text') ?? '';
            if (!text) return;
            e.preventDefault();
            if (!open) openPanel();
            const el = e.currentTarget;
            const clean = text.replace(/\s+/g, ' ').trim();
            const start = el.selectionStart ?? query.length;
            const end = el.selectionEnd ?? query.length;
            setQuery((open || controlled ? query.slice(0, start) + clean + query.slice(end) : clean).trim());
          }}
          onKeyDown={(e) => {
            if (e.key === 'Escape') {
              close();
              inputRef.current?.blur();
            } else if (e.key === 'Enter' && onSearch) {
              e.preventDefault();
              if (!open) openPanel();
              onSearch();
            } else if (e.key === 'Enter' && open) {
              e.preventDefault();
              if (listOptions.length === 1) pick(listOptions[0]);
              else {
                const exact = listOptions.find((o) => normalizeSearchText(o.label) === q);
                if (exact) pick(exact);
              }
            }
          }}
        />
        <button
          type="button"
          disabled={disabled}
          aria-label={open ? 'Close list' : 'Open list'}
          onMouseDown={(e) => {
            e.preventDefault(); // don't steal focus / cause a blur-close race
            if (disabled) return;
            if (open) close();
            else {
              openPanel();
              inputRef.current?.focus();
            }
          }}
          style={{
            position: 'absolute',
            right: 6,
            top: '50%',
            transform: 'translateY(-50%)',
            border: 'none',
            background: 'transparent',
            color: 'var(--muted)',
            cursor: disabled ? 'default' : 'pointer',
            padding: 4,
            lineHeight: 1,
          }}
        >
          <i className={'fa-solid ' + (open ? 'fa-chevron-up' : 'fa-chevron-down')} />
        </button>
      </div>
      {onSearch && (
        <button
          type="button"
          className="btn sm primary"
          disabled={disabled || loading || query.trim().length < 2}
          onMouseDown={(e) => {
            e.preventDefault(); // keep the list open / the box focused
            if (disabled || loading || query.trim().length < 2) return;
            if (!open) openPanel();
            inputRef.current?.focus();
            onSearch();
          }}
          onClick={(e) => {
            // Keyboard (Enter / Space on the button) -- the mouse is handled on mousedown above.
            if (e.detail !== 0 || disabled || loading || query.trim().length < 2) return;
            if (!open) openPanel();
            onSearch();
          }}
          style={{ whiteSpace: 'nowrap' }}
        >
          <i className={'fa-solid ' + (loading ? 'fa-spinner fa-spin' : 'fa-magnifying-glass')} /> Search
        </button>
      )}

      {panel && createPortal(panel, document.body)}
    </div>
  );
}
