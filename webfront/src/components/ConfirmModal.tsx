import type { ReactNode } from 'react';
import Modal, { ModalHeader } from './Modal';

/* In-app confirmation popup — replaces the browser's window.confirm() ("localhost:5173 says…"),
   which can't be styled, shows the site address instead of a title, and looks different on every
   browser. Built on the shared Modal, so it gets the same focus trap / Esc / click-outside close. */
export default function ConfirmModal({
  open,
  title,
  message,
  confirmLabel = 'Delete',
  cancelLabel = 'Cancel',
  danger = true,
  busy = false,
  onConfirm,
  onCancel,
}: {
  open: boolean;
  title: ReactNode;
  message: ReactNode;
  confirmLabel?: string;
  cancelLabel?: string;
  danger?: boolean;
  busy?: boolean;
  onConfirm: () => void;
  onCancel: () => void;
}) {
  const close = () => { if (!busy) onCancel(); };
  return (
    <Modal open={open} onClose={close}>
      <ModalHeader title={title} onClose={close} />
      <div className="card-b confirm-body">
        <div className="confirm-msg">
          {danger && <i className="fa-solid fa-triangle-exclamation confirm-icon" aria-hidden="true" />}
          <div>{message}</div>
        </div>
        <div className="confirm-actions">
          <button className="btn sm" onClick={close} disabled={busy}>{cancelLabel}</button>
          <button className={'btn sm ' + (danger ? 'danger' : 'primary')} onClick={onConfirm} disabled={busy}>
            {busy ? <><i className="fa-solid fa-spinner fa-spin" /> Working…</> : confirmLabel}
          </button>
        </div>
      </div>
    </Modal>
  );
}
