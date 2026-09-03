/* Temporary placeholder for pages not yet ported (Phases B–E). */
export default function PagePlaceholder({ title, phase }: { title: string; phase: string }) {
  return (
    <div className="card">
      <div className="card-h">
        <h2>{title}</h2>
      </div>
      <div className="empty">This page will be ported from the existing frontend in {phase}</div>
    </div>
  );
}
