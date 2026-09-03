import { intFmt } from '../../utils/format';

/* Ported from statTile() in app.js — icon chip, label, big value, WoW trend. */
export default function StatTile({
  icon,
  bg,
  fg,
  label,
  value,
  pct,
}: {
  icon: string;
  bg: string;
  fg: string;
  label: string;
  value: number;
  pct: number;
}) {
  const up = (pct || 0) >= 0;
  return (
    <div className="card" style={{ marginBottom: 0 }}>
      <div className="card-b" style={{ padding: 18 }}>
        <div
          style={{
            width: 38,
            height: 38,
            borderRadius: 'var(--r3)',
            background: bg,
            color: fg,
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
            fontSize: 16,
            marginBottom: 12,
          }}
        >
          {icon}
        </div>
        <div className="hint" style={{ marginBottom: 2 }}>
          {label}
        </div>
        <div style={{ fontSize: 26, fontWeight: 700, lineHeight: '34px', marginBottom: 6 }}>
          {intFmt(value)}
        </div>
        <div style={{ fontSize: 12, color: up ? 'var(--green)' : 'var(--red)' }}>
          {up ? '▲' : '▼'} {Math.abs(pct || 0)}% จากสัปดาห์ที่แล้ว
        </div>
      </div>
    </div>
  );
}
