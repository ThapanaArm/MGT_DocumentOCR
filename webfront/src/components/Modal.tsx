import { useEffect, type ReactNode } from 'react';

/* Reusable modal — ports the #ov/.modal overlay + openModal/closeModal. */
export default function Modal({
  open,
  onClose,
  wide,
  children,
}: {
  open: boolean;
  onClose: () => void;
  wide?: boolean;
  children: ReactNode;
}) {
  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [open, onClose]);

  return (
    <div
      className={'ov' + (open ? ' on' : '')}
      onClick={(e) => {
        if ((e.target as HTMLElement).classList.contains('ov')) onClose();
      }}
    >
      {open && <div className={'modal' + (wide ? ' wide' : '')}>{children}</div>}
    </div>
  );
}

/* Standard modal header with title + close button. */
export function ModalHeader({ title, onClose }: { title: ReactNode; onClose: () => void }) {
  return (
    <div className="card-h">
      <h2>{title}</h2>
      <div className="sp" />
      <button className="btn sm" onClick={onClose}>
        ✕
      </button>
    </div>
  );
}
