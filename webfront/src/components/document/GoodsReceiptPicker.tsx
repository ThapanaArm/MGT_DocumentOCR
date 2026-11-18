import { useEffect, useState } from 'react';
import { api as apiClient } from '../../api/client';

/* Goods Receipt (Material Doc.) / Fiscal Year straight from SAP (AP, 2026-11-18): the PO's
   receipts are listed from API_MATERIAL_DOCUMENT_SRV. With exactly one receipt that is not
   reversed and nothing typed yet, it is filled in by itself; otherwise AP picks one. */

type Receipt = {
  matDoc: string;
  year: string;
  postingDate: string;
  movementType: string;
  qty: number;
  unit: string;
  poItems: string[];
  reversed: boolean;
};

export default function GoodsReceiptPicker({
  po,
  matDoc,
  locked,
  onPick,
}: {
  po: string;
  matDoc: string;
  locked: boolean;
  onPick: (matDoc: string, year: string) => void;
}) {
  const [list, setList] = useState<Receipt[] | null>(null);
  const [msg, setMsg] = useState('');
  const [loading, setLoading] = useState(false);

  const load = async (auto: boolean) => {
    const p = po.trim();
    if (!p) return;
    setLoading(true);
    setMsg('');
    try {
      const r = await apiClient.get<{ configured: boolean; receipts: Receipt[] }>(
        `/api/sap/po/${encodeURIComponent(p)}/goods-receipts`,
      );
      if (!r.configured) {
        setList([]);
        setMsg('ยังไม่ได้ตั้งค่า SAP Material Document API — กรอกเองได้');
        return;
      }
      setList(r.receipts);
      const active = r.receipts.filter((x) => !x.reversed);
      if (active.length === 0) setMsg(`PO ${p} ยังไม่มี Goods Receipt ใน SAP`);
      else if (auto && active.length === 1 && !matDoc.trim() && !locked) {
        onPick(active[0].matDoc, active[0].year);
        setMsg(`ดึงจาก SAP แล้ว: ${active[0].matDoc} / ${active[0].year}`);
      }
    } catch (e) {
      setList(null);
      setMsg(e instanceof Error ? e.message : String(e));
    } finally {
      setLoading(false);
    }
  };

  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => { void load(true); }, [po]);

  const fmtDate = (d: string) => (/^\d{4}-\d{2}-\d{2}$/.test(d) ? d.split('-').reverse().join('.') : d);

  return (
    <div style={{ display: 'grid', gap: 6 }}>
      <div className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
        <button className="btn sm ghost" disabled={loading || !po.trim()} onClick={() => void load(false)}
          title="อ่าน Goods Receipt ของ PO นี้จาก SAP">
          <i className={loading ? 'fa-solid fa-spinner fa-spin' : 'fa-solid fa-rotate'} /> ดึง GR จาก SAP
        </button>
        {(list || []).map((r) => {
          const on = r.matDoc === matDoc.trim();
          return (
            <button
              key={`${r.matDoc}-${r.year}`}
              className={'btn sm' + (on ? '' : ' ghost')}
              disabled={locked || r.reversed}
              onClick={() => onPick(r.matDoc, r.year)}
              title={`${r.movementType} · PO item ${r.poItems.join(', ')}${r.reversed ? ' · ถูกกลับรายการแล้ว' : ''}`}
              style={r.reversed ? { textDecoration: 'line-through' } : undefined}
            >
              {r.matDoc} / {r.year} · {fmtDate(r.postingDate)} · {r.qty.toLocaleString()} {r.unit}
            </button>
          );
        })}
      </div>
      {msg && <span className="hint">{msg}</span>}
    </div>
  );
}
