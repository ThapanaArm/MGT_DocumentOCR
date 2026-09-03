/* Ports pagerHtml() — page-size select + prev/next. */
export default function Pager({
  page,
  setPage,
  pageSize,
  setPageSize,
  total,
}: {
  page: number;
  setPage: (n: number) => void;
  pageSize: number;
  setPageSize: (n: number) => void;
  total: number;
}) {
  const totalPages = Math.max(1, Math.ceil(total / pageSize));
  const cur = Math.min(Math.max(1, page), totalPages);
  return (
    <div
      className="row"
      style={{ justifyContent: 'space-between', alignItems: 'center', marginTop: 12, flexWrap: 'wrap', gap: 10 }}
    >
      <div className="row" style={{ gap: 6, alignItems: 'center' }}>
        <span className="hint">แสดง</span>
        <select
          value={pageSize}
          onChange={(e) => {
            setPageSize(parseInt(e.target.value));
            setPage(1);
          }}
        >
          {[10, 50, 100].map((n) => (
            <option key={n} value={n}>
              {n}
            </option>
          ))}
        </select>
        <span className="hint">รายการ/หน้า · ทั้งหมด {total} รายการ</span>
      </div>
      <div className="row" style={{ gap: 6, alignItems: 'center' }}>
        <button className="btn sm ghost" onClick={() => setPage(Math.max(1, cur - 1))} disabled={cur <= 1}>
          ‹ ก่อนหน้า
        </button>
        <span className="hint">
          หน้า {cur} / {totalPages}
        </span>
        <button
          className="btn sm ghost"
          onClick={() => setPage(Math.min(totalPages, cur + 1))}
          disabled={cur >= totalPages}
        >
          ถัดไป ›
        </button>
      </div>
    </div>
  );
}

export function paginate<T>(list: T[], page: number, pageSize: number): T[] {
  const totalPages = Math.max(1, Math.ceil(list.length / pageSize));
  const cur = Math.min(Math.max(1, page), totalPages);
  const start = (cur - 1) * pageSize;
  return list.slice(start, start + pageSize);
}

/* Ports dateRangeHtml() — from/to date inputs with clear button. */
export function DateRange({
  from,
  to,
  setFrom,
  setTo,
}: {
  from: string;
  to: string;
  setFrom: (v: string) => void;
  setTo: (v: string) => void;
}) {
  return (
    <>
      <input type="date" value={from} onChange={(e) => setFrom(e.target.value)} title="วันที่เริ่มต้น" />
      <span className="hint">ถึง</span>
      <input type="date" value={to} onChange={(e) => setTo(e.target.value)} title="วันที่สิ้นสุด" />
      {(from || to) && (
        <button
          className="btn sm ghost"
          onClick={() => {
            setFrom('');
            setTo('');
          }}
          title="ล้างช่วงวันที่"
        >
          ✕
        </button>
      )}
    </>
  );
}

export function inDateRange(dateStr: unknown, from: string, to: string): boolean {
  const d = String(dateStr || '').slice(0, 10);
  if (from && (!d || d < from)) return false;
  if (to && (!d || d > to)) return false;
  return true;
}
