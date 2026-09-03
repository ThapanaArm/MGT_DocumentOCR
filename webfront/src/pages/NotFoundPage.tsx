import { Link } from 'react-router-dom';
export default function NotFoundPage() {
  return (
    <div className="card">
      <div className="empty">
        <p style={{ fontSize: 18, fontWeight: 600 }}>Page not found (404)</p>
        <Link className="btn primary" to="/">Back to Overview</Link>
      </div>
    </div>
  );
}
