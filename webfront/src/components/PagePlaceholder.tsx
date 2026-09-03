/* Temporary placeholder for pages not yet ported (Phases B–E). */
export default function PagePlaceholder({ title, phase }: { title: string; phase: string }) {
  return (
    <div className="card">
      <div className="card-h">
        <h2>{title}</h2>
      </div>
      <div className="empty">หน้านี้จะพอร์ตจาก frontend เดิมใน {phase}</div>
    </div>
  );
}
