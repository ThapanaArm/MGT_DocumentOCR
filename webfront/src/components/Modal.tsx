import { useEffect, useRef, type ReactNode } from 'react';

/* Reusable modal — ports the #ov/.modal overlay + openModal/closeModal.
   Accessibility (F08): the dialog carries role="dialog" + aria-modal, moves focus into itself on
   open, traps Tab within it, and restores focus to the previously focused element on close. */
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
  const modalRef = useRef<HTMLDivElement>(null);
  const lastFocused = useRef<HTMLElement | null>(null);
  // Keep onClose in a ref so the focus/keydown effect depends only on `open` — otherwise a new
  // inline onClose each render would re-run the effect and steal focus back to the first control
  // while the user is typing.
  const onCloseRef = useRef(onClose);
  onCloseRef.current = onClose;

  useEffect(() => {
    if (!open) return;
    lastFocused.current = document.activeElement as HTMLElement | null;

    const FOCUSABLE =
      'a[href],button:not([disabled]),textarea:not([disabled]),input:not([disabled]),select:not([disabled]),[tabindex]:not([tabindex="-1"])';
    const focusable = () =>
      Array.from(modalRef.current?.querySelectorAll<HTMLElement>(FOCUSABLE) ?? []).filter(
        (el) => el.offsetParent !== null || el === document.activeElement,
      );

    // Move focus into the dialog once it's mounted.
    const first = focusable()[0];
    (first ?? modalRef.current)?.focus();

    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        onCloseRef.current();
        return;
      }
      if (e.key !== 'Tab') return;
      const node = modalRef.current;
      if (!node) return;
      const items = focusable();
      if (items.length === 0) {
        e.preventDefault();
        node.focus();
        return;
      }
      const firstEl = items[0];
      const lastEl = items[items.length - 1];
      const active = document.activeElement as HTMLElement | null;
      if (e.shiftKey && (active === firstEl || !node.contains(active))) {
        e.preventDefault();
        lastEl.focus();
      } else if (!e.shiftKey && (active === lastEl || !node.contains(active))) {
        e.preventDefault();
        firstEl.focus();
      }
    };

    window.addEventListener('keydown', onKey);
    return () => {
      window.removeEventListener('keydown', onKey);
      lastFocused.current?.focus?.();
    };
  }, [open]);

  return (
    <div
      className={'ov' + (open ? ' on' : '')}
      onClick={(e) => {
        if ((e.target as HTMLElement).classList.contains('ov')) onClose();
      }}
    >
      {open && (
        <div
          ref={modalRef}
          className={'modal' + (wide ? ' wide' : '')}
          role="dialog"
          aria-modal="true"
          tabIndex={-1}
        >
          {children}
        </div>
      )}
    </div>
  );
}

/* Standard modal header with title + close button. */
export function ModalHeader({ title, onClose }: { title: ReactNode; onClose: () => void }) {
  return (
    <div className="card-h">
      <h2>{title}</h2>
      <div className="sp" />
      <button className="btn sm" onClick={onClose} aria-label="Close" title="Close">
        <i className="fa-solid fa-xmark" />
      </button>
    </div>
  );
}
