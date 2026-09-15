import { Component, type ErrorInfo, type ReactNode } from 'react';

/* =====================================================================
   Catches render-time errors anywhere below it and shows a readable
   fallback instead of a blank white page. A single buggy component
   should never take the whole app down — especially one that posts to SAP.
   ===================================================================== */

interface State {
  error: Error | null;
}

export default class ErrorBoundary extends Component<{ children: ReactNode }, State> {
  state: State = { error: null };

  static getDerivedStateFromError(error: Error): State {
    return { error };
  }

  componentDidCatch(error: Error, info: ErrorInfo): void {
    // Kept in the console so the real stack is still available while developing.
    console.error('Unhandled UI error:', error, info.componentStack);
  }

  render() {
    if (!this.state.error) return this.props.children;
    return (
      <div style={{ minHeight: '100vh', display: 'grid', placeItems: 'center', background: 'var(--bg)', color: 'var(--text)', padding: 24 }}>
        <div style={{ maxWidth: 440, textAlign: 'center' }}>
          <div style={{ fontSize: 34, marginBottom: 10 }}>⚠️</div>
          <h1 style={{ fontSize: 19, margin: '0 0 8px' }}>Something went wrong</h1>
          <p style={{ color: 'var(--muted, #6b7b70)', fontSize: 14, margin: '0 0 18px' }}>
            Something went wrong. Try reloading the page. If it keeps happening, contact IT.
          </p>
          <pre style={{ textAlign: 'left', fontSize: 12, background: 'var(--card, #fff)', border: '1px solid var(--line, #e4e7e5)', borderRadius: 8, padding: '10px 12px', overflow: 'auto', maxHeight: 160, color: 'var(--red, #bd120f)' }}>
            {this.state.error.message}
          </pre>
          <button
            type="button"
            className="btn primary"
            onClick={() => window.location.reload()}
            style={{ marginTop: 16, padding: '9px 20px' }}
          >
            Reload page
          </button>
        </div>
      </div>
    );
  }
}
