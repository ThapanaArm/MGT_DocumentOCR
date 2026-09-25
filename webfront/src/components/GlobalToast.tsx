import { useEffect, useRef } from 'react';
import { useAppState } from '../state/AppState';

/* Global toast — ports the old #toast element + toast() behavior. */
export default function GlobalToast() {
  const { toast, hideToast } = useAppState();
  const timerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const startedAtRef = useRef(0);
  const remainingRef = useRef(4500);

  useEffect(() => {
    if (!toast.open) return;
    remainingRef.current = toast.type === 'error' ? 6000 : 4500;
    startedAtRef.current = Date.now();
    timerRef.current = setTimeout(hideToast, remainingRef.current);
    return () => { if (timerRef.current) clearTimeout(timerRef.current); };
  }, [toast.open, toast.message, toast.type, hideToast]);

  const pauseCountdown = () => {
    if (!timerRef.current) return;
    clearTimeout(timerRef.current);
    timerRef.current = null;
    remainingRef.current = Math.max(0, remainingRef.current - (Date.now() - startedAtRef.current));
  };

  const resumeCountdown = () => {
    if (!toast.open || timerRef.current || remainingRef.current <= 0) return;
    startedAtRef.current = Date.now();
    timerRef.current = setTimeout(hideToast, remainingRef.current);
  };

  const icon = toast.type === 'success' ? 'fa-check' : toast.type === 'error' ? 'fa-xmark' : 'fa-info';
  const message = toast.message.trim();
  const title = (() => {
    if (toast.type === 'error') return 'Unable to complete the action';
    if (/please|กรุณา|ต้องเลือก|ต้องระบุ/i.test(message)) return 'Action required';
    if (/incomplete|ยังไม่ครบ|ไม่สมบูรณ์/i.test(message)) return 'Review required';
    if (/split|แยก/i.test(message)) return 'Document split completed';
    if (/created|สร้าง/i.test(message)) return 'Created successfully';
    if (/saved|บันทึก/i.test(message)) return 'Saved successfully';
    if (/deleted|ลบ/i.test(message)) return 'Deleted successfully';
    if (/added|เพิ่ม/i.test(message)) return 'Added successfully';
    if (/queued|เข้าคิว/i.test(message)) return 'Queued for processing';
    if (/selected|เลือก/i.test(message)) return 'Selection updated';
    if (/complete|สำเร็จ/i.test(message)) return 'Completed successfully';
    return toast.type === 'success' ? 'Completed successfully' : 'Information';
  })();

  return (
    <div
      key={`${toast.type}:${toast.message}`}
      className={`toast toast-${toast.type}${toast.open ? ' on' : ''}`}
      id="toast"
      role={toast.type === 'error' ? 'alert' : 'status'}
      aria-live={toast.type === 'error' ? 'assertive' : 'polite'}
      onMouseEnter={pauseCountdown}
      onMouseLeave={resumeCountdown}
    >
      <span className="toast-icon"><i className={`fa-solid ${icon}`} /></span>
      <span className="toast-copy"><b>{title}</b><span>{message}</span></span>
      <button type="button" className="toast-close" onClick={hideToast} aria-label="Close notification">
        <i className="fa-solid fa-xmark" />
      </button>
    </div>
  );
}
