import { useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';

export interface MaterialSearchOption {
  value: string;
  description: string;
  label: string;
  /** Explicit fields used for matching when display-only text must not affect the results. */
  searchText?: string;
  source?: 'local' | 'sap';
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
  searchPlaceholder = 'พิมพ์ชื่อหรือรหัสเพื่อค้นหา…',
  ariaLabel = 'ค้นหาและเลือกรายการ',
  minWidth = 300,
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
}) {
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState(''); // what the person is typing while the panel is open
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
  const selectedLabel = selected?.label ?? (value || '');

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
    setQuery('');
  };
  const close = () => {
    setOpen(false);
    setQuery('');
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
        if (active) setSapError(e instanceof Error ? e.message : 'ค้นหา SAP ไม่สำเร็จ');
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
  const localVisible = !q
    ? options
    : options.filter(
        (o) => o.searchText !== undefined
          ? normalizeSearchText(o.searchText).includes(q)
          : normalizeSearchText(o.value).includes(q) || normalizeSearchText(o.label).includes(q),
      );

  const listOptions = [
    ...localVisible,
    ...sapOptions.filter(
      (s) =>
        (!q || normalizeSearchText(s.value).includes(q) || normalizeSearchText(s.label).includes(q)) &&
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
          ปัจจุบัน: {selectedLabel}
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
          {sapLoading ? 'กำลังค้นหา…' : 'ไม่พบรายการ — ลองพิมพ์คำค้นอื่น'}
        </div>
      )}
      {listOptions.map((o, index) => {
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
          <div
            key={key}
            style={rowStyle(hover === key, o.value === value)}
            onMouseEnter={() => setHover(key)}
            onMouseLeave={() => setHover(null)}
            onMouseDown={(e) => {
              e.preventDefault(); // fire before the input's blur so the pick isn't lost
              pick(o);
            }}
          >
            {o.label}
            {o.source === 'sap' && (
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
                SAP
              </span>
            )}
            {o.value === value && (
              <i className="fa-solid fa-check" style={{ marginLeft: 6, color: 'var(--brand)' }} />
            )}
          </div>
        );
      })}
      {searchSap && q.length >= 2 && (
        <div style={{ padding: '6px 10px', fontSize: 12, color: 'var(--muted)' }}>
          {sapLoading ? 'ค้นหาจาก SAP…' : `พบใน SAP ${sapOptions.length} รายการ`}
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
        แสดง {listOptions.length} / {options.length} รายการ
        {q ? ` · ค้นหา “${query.trim()}”` : ''}
      </div>
    </div>
  );

  return (
    <div ref={rootRef} style={{ position: 'relative', minWidth }}>
      <div style={{ position: 'relative' }}>
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
          value={open ? query : selectedLabel}
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
          onKeyDown={(e) => {
            if (e.key === 'Escape') {
              close();
              inputRef.current?.blur();
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
          aria-label={open ? 'ปิดรายการ' : 'เปิดรายการ'}
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

      {panel && createPortal(panel, document.body)}
    </div>
  );
}
