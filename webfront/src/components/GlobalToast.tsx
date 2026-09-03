import { useEffect } from 'react';
import { useAppState } from '../state/AppState';

/* Global toast — ports the old #toast element + toast() behavior. */
export default function GlobalToast() {
  const { toast, hideToast } = useAppState();

  useEffect(() => {
    if (!toast.open) return;
    const t = setTimeout(hideToast, 3500);
    return () => clearTimeout(t);
  }, [toast.open, toast.message, hideToast]);

  return (
    <div className={'toast' + (toast.open ? ' on' : '')} id="toast">
      {toast.message}
    </div>
  );
}
