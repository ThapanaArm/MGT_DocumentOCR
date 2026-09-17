import { forwardRef, useImperativeHandle, useRef, useState } from 'react';
import { getPayload, postToSap, type DocModel, type MapResult } from '../../../api/documents';
import { useAppState } from '../../../state/AppState';
import Modal, { ModalHeader } from '../../Modal';
import SapSalesOrderEditor from '../SapSalesOrderEditor';

const USER = '(ignored by the server)';

export interface SalesOrderStepHandle {
  open: () => void;
  stageMaterialMatch?: (lineIndex: number, item: unknown) => void;
}

interface Props {
  doc: DocModel;
  map: MapResult;
  salesOrg: string;
  posted: boolean;
  onPosted: (doc: DocModel) => void;
}

const SapSalesOrderStep = forwardRef<SalesOrderStepHandle, Props>(function SapSalesOrderStep(
  { doc, map, salesOrg, posted, onPosted },
  ref,
) {
  const { guard, showToast } = useAppState();
  const [visible, setVisible] = useState(false);
  const [posting, setPosting] = useState(false);
  const [payload, setPayload] = useState<Record<string, unknown> | null>(null);
  const editorRef = useRef<HTMLDivElement | null>(null);

  useImperativeHandle(ref, () => ({
    open() {
      setVisible(true);
      setTimeout(() => editorRef.current?.scrollIntoView({ behavior: 'smooth', block: 'start' }), 60);
    },
  }), []);

  const send = () =>
    guard(async () => {
      setPosting(true);
      try {
        const result = await postToSap(doc.docId, USER);
        onPosted(result.document);
        showToast(
          (result.simulated ? '(Simulation Mode) ' : '') +
            'Document created in SAP successfully — No. ' +
            result.sapDocNo,
        );
        window.scrollTo({ top: 0, behavior: 'smooth' });
      } finally {
        setPosting(false);
      }
    });

  const viewPayload = () =>
    guard(async () => {
      const result = await getPayload(doc.docId);
      setPayload(result.payload);
    });

  return (
    <>
      {visible && (
        <div ref={editorRef}>
          <SapSalesOrderEditor
            doc={doc}
            map={map}
            header={doc.header}
            salesOrg={salesOrg}
            sending={posting}
            posted={posted}
            onSend={send}
            onViewPayload={viewPayload}
          />
        </div>
      )}

      <Modal open={payload != null} onClose={() => setPayload(null)}>
        <ModalHeader title="Payload to Send to SAP" onClose={() => setPayload(null)} />
        <div className="card-b">
          <p className="hint">
            Endpoint: <code>{String(payload?._target ?? '')}</code>
          </p>
          <pre className="json">{JSON.stringify(payload, null, 2)}</pre>
        </div>
      </Modal>
    </>
  );
});

export default SapSalesOrderStep;
