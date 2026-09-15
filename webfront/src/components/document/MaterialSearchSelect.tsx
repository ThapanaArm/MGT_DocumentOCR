import { useEffect, useMemo, useRef, useState } from 'react';

export interface MaterialSearchOption {
  value: string;
  description: string;
  label: string;
  source?: 'local' | 'sap';
}

/* A searchable dropdown (custom combobox) for picking a Material.

   Not a native <input list> + <datalist>: the browser filters a datalist by the input's own text,
   and the box shows the selected option's full label, so opening it filtered every option against
   that whole label and the list collapsed to just the current item (or wouldn't open). This is a
   real click-to-open panel we render ourselves instead.

   The panel is position:fixed, anchored to the input's on-screen box. That's on purpose: the DETAIL
   table sits inside a `.tw { overflow:auto }` wrapper, and an absolutely-positioned panel would be
   clipped by it. Fixed positioning escapes the overflow so the list is fully visible and clickable
   in the table too, not just in the Mapping cards. */
export default function MaterialSearchSelect({
  options,
  value,
  disabled,
  onChange,
  searchSap,
  emptyLabel = '-- Not found / Please select --',
}: {
  options: MaterialSearchOption[];
  value: string;
  disabled?: boolean;
  onChange: (value: string, option?: MaterialSearchOption) => void;
  searchSap?: (keyword: string) => Promise<MaterialSearchOption[]>;
  emptyLabel?: string;
}) {
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState(''); // what the person is typing while the panel is open
  const [sapOptions, setSapOptions] = useState<MaterialSearchOption[]>([]);
  const [sapLoading, setSapLoading] = useState(false);
  const [sapError, setSapError] = useState('');
  const [hover, setHover] = useState<string | null>(null);
  const [rect, setRect] = useState<{ top: number; left: number; width: number } | null>(null);
  const rootRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLInputElement>(null);

  const findOpt = (v: string) => [...options, ...sapOptions].find((o) => o.value === v);
  const selected = value ? findOpt(value) : undefined;
  const selectedLabel = selected?.label ?? (value || '');

  const reposition = () => {
    const el = inputRef.current;
    if (!el) return;
    const r = el.getBoundingClientRect();
    setRect({ top: r.bottom + 4, left: r.left, width: r.width });
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
  useEffect(() => {
    if (!open) return;
    const onDocDown = (e: MouseEvent) => {
      if (rootRef.current && !rootRef.current.contains(e.target as Node)) close();
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

  const q = query.trim().toLocaleLowerCase();
  const localVisible = useMemo(() => {
    if (!q) return options;
    // Match anywhere in the code or description -- the typed text can be at the start, middle, or
    // end, all treated equally (no positional ranking). Keep the original list order (this customer's
    // own materials first, then alphabetical) rather than reshuffling by where the match landed.
    return options.filter(
      (o) =>
        o.value.toLocaleLowerCase().includes(q) ||
        o.description.toLocaleLowerCase().includes(q) ||
        o.label.toLocaleLowerCase().includes(q),
    );
  }, [q, options]);

  const listOptions = [
    ...localVisible,
    ...sapOptions.filter((s) => !localVisible.some((o) => o.value === s.value)),
  ];

  const pick = (o: MaterialSearchOption) => {
    if (o.value !== value) onChange(o.value, o);
    close();
  };

  const rowStyle = (active: boolean, isSelected: boolean): React.CSSProperties => ({
    padding: '8px 10px',
    cursor: 'pointer',
    fontSize: 13,
    lineHeight: 1.35,
    background: active ? 'var(--brand-soft)' : isSelected ? 'var(--line-soft)' : 'transparent',
    color: 'var(--text)',
    borderRadius: 6,
  });

  return (
    <div ref={rootRef} style={{ position: 'relative', minWidth: 260 }}>
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
          placeholder={open ? 'พิมพ์ชื่อหรือรหัสเพื่อค้นหา…' : emptyLabel}
          aria-label="ค้นหาและเลือก Material"
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
                const exact = listOptions.find((o) => o.label.toLocaleLowerCase() === q);
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

      {open && rect && (
        <div
          style={{
            position: 'fixed',
            top: rect.top,
            left: rect.left,
            width: rect.width,
            zIndex: 1000,
            background: 'var(--card)',
            border: '1px solid var(--line)',
            borderRadius: 'var(--r3)',
            boxShadow: 'var(--sh3)',
            maxHeight: 300,
            overflowY: 'auto',
            padding: 4,
          }}
        >
          {selectedLabel && (
            <div style={{ padding: '4px 10px 6px', fontSize: 11, color: 'var(--muted)' }}>
              ปัจจุบัน: {selectedLabel}
            </div>
          )}
          {listOptions.length === 0 && (
            <div style={{ padding: '10px', fontSize: 13, color: 'var(--muted)' }}>
              {sapLoading ? 'กำลังค้นหา…' : 'ไม่พบรายการ — ลองพิมพ์คำค้นอื่น'}
            </div>
          )}
          {listOptions.map((o) => {
            const key = (o.source === 'sap' ? 'sap:' : 'local:') + o.value;
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
      )}
    </div>
  );
}
