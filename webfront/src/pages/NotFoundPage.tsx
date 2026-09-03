import { Link } from 'react-router-dom';
export default function NotFoundPage() {
  return (
    <div className="card">
      <div className="empty">
        <p style={{ fontSize: 18, fontWeight: 600 }}>ไม่พบหน้านี้ (404)</p>
        <Link className="btn primary" to="/">กลับหน้า Overview</Link>
      </div>
    </div>
  );
}
