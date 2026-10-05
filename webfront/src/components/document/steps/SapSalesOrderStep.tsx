import { forwardRef, useEffect, useImperativeHandle, useRef, useState } from 'react';
import { getSoPayloads, getSoPosts, postSalesOrderGroups, type DocModel, type MapResult, type SoGroupState } from '../../../api/documents';
import { useAppState } from '../../../state/AppState';
import Modal, { ModalHeader } from '../../Modal';
import SapSalesOrderEditor from '../SapSalesOrderEditor';

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
  const [payload, setPayload] = useState<unknown>(null);
  // Send results per delivery-date group (one SAP Sales Order per date, all from this document).
  const [posts, setPosts] = useState<SoGroupState[]>([]);
  const reloadPosts = () => getSoPosts(doc.docId).then((r) => setPosts(r.groups)).catch(() => setPosts([]));
  useEffect(() => { void reloadPosts(); /* eslint-disable-next-line react-hooks/exhaustive-deps */ }, [doc.docId, doc.status]);
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
        const result = await postSalesOrderGroups(doc.docId);
        onPosted(result.document);
        await reloadPosts();
        const sent = result.results.filter((g) => g.success && !g.skipped);
        const failed = result.results.filter((g) => !g.success);
        const nos = sent.map((g) => g.docNo).filter(Boolean).join(', ');
        if (failed.length === 0) {
          showToast(
            `${result.simulated ? 'Simulation completed' : `Created ${sent.length} Sales Order${sent.length === 1 ? '' : 's'} in SAP`}${nos ? ` — ${nos}` : ''}`,
            'success',
          );
        } else {
          showToast(
            `${sent.length} Sales Order${sent.length === 1 ? '' : 's'} created. ${failed.length} require attention. Submit again to retry the remaining orders.`,
            'error',
          );
        }
        document.querySelector('.content')?.scrollTo({ top: 0, behavior: 'smooth' }); window.scrollTo({ top: 0, behavior: 'smooth' });
      } finally {
        setPosting(false);
      }
    });

  const viewPayload = () =>
    guard(async () => {
      const result = await getSoPayloads(doc.docId);
      // One delivery date -> the single payload as before; several -> one payload per Sales Order.
      setPayload(result.payloads.length === 1
        ? result.payloads[0].payload
        : result.payloads.map((p) => ({ deliveryDate: p.key || null, items: p.itemNos, payload: p.payload })));
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
            posts={posts}
            partial={doc.status === 'PARTIAL'}
          />
        </div>
      )}

      <Modal open={payload != null} onClose={() => setPayload(null)}>
        <ModalHeader title="Payload to Send to SAP" onClose={() => setPayload(null)} />
        <div className="card-b">
          <p className="hint">
            Review the data prepared for submission
            {Array.isArray(payload) && <> · {payload.length} Sales Orders grouped by delivery date</>}
          </p>
          <pre className="json">{JSON.stringify(payload, null, 2)}</pre>
        </div>
      </Modal>
    </>
  );
});

export default SapSalesOrderStep;
