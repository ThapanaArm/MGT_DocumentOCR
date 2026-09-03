import { useEffect, useRef, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { useAppState } from '../state/AppState';
import { useMeta } from '../state/MetaContext';
import { MODULE_LABEL } from '../navConfig';
import { moduleLabel } from '../utils/format';
import { sampleDocument, uploadDocument } from '../api/documents';
import type { ModuleCode } from '../api/types';
import Steps from '../components/Steps';
import OcrProviderSelect from '../components/OcrProviderSelect';

/* Ports renderWork()'s upload screen (uploadHtml + bindDrop + uploadFile). */

const USER = 'it-digital@megachem.co.th';

export default function ImportPage() {
  const { module } = useParams<{ module: ModuleCode }>();
  const mod = (module ?? 'AP') as ModuleCode;
  const navigate = useNavigate();
  const { guard, showToast } = useAppState();
  const { ocrProviders, loadOcrProviders, apDocCategories, loadApDocCategories } = useMeta();

  const [provider, setProvider] = useState('auto');
  const [category, setCategory] = useState('');
  const [dragOver, setDragOver] = useState(false);
  const [progress, setProgress] = useState<{ text: string; pct: number } | null>(null);
  const fileRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    loadOcrProviders();
    if (mod === 'AP') loadApDocCategories();
    setCategory('');
  }, [mod, loadOcrProviders, loadApDocCategories]);

  const needCategory = mod === 'AP' && !category;
  const providers = ocrProviders ?? [];
  const active = providers.find((p) => p.id === provider) || providers[0];

  async function handleFile(file: File) {
    if (mod === 'AP' && !category) {
      showToast('⚠ Please select a document type first');
      return;
    }
    const fd = new FormData();
    fd.append('module', mod);
    fd.append('user', USER);
    fd.append('ocr_', provider);
    fd.append('file', file);
    fd.append('apDocCategory', category || '');
    setProgress({ text: 'Uploading ' + file.name + ' …', pct: 35 });
    const doc = await guard(async () => {
      setProgress({ text: 'Reading document (' + provider + ') …', pct: 70 });
      return uploadDocument(fd);
    });
    setProgress(null);
    if (doc) {
      if (doc.provider === 'failed')
        showToast('⚠ Failed to read document — attach an image in the AI chat on the document page for help filling it in');
      else showToast('✓ Document read successfully (' + doc.provider + ') — found ' + doc.lines.length + ' items');
      navigate('/doc/' + doc.docId);
    }
  }

  async function useSample(i: number) {
    if (mod === 'AP' && !category) {
      showToast('⚠ Please select a document type first');
      return;
    }
    const doc = await guard(() =>
      sampleDocument({ module: mod, index: i, user: USER, apDocCategory: category }),
    );
    if (doc) {
      showToast('✓ Document created in the system (DocId ' + doc.docId + ')');
      navigate('/doc/' + doc.docId);
    }
  }

  return (
    <>
      <Steps current={1} />
      <div className="card">
        <div className="card-h">
          <h2>Step 1 — Import Document</h2>
          <div className="sp" />
          <span className="hint">Supports PDF / JPG / PNG / TIFF</span>
        </div>
        <div className="card-b">
          {mod === 'AP' && (
            <div className="row" style={{ marginBottom: 16 }}>
              <label className="hint" style={{ fontWeight: 600 }}>
                📋 Document Type
              </label>
              <select value={category} onChange={(e) => setCategory(e.target.value)}>
                <option value="">— Select Document Type —</option>
                {(apDocCategories ?? []).map((c) => (
                  <option key={c.id} value={c.id}>
                    {c.label}
                  </option>
                ))}
              </select>
            </div>
          )}
          {needCategory && (
            <p className="hint" style={{ margin: '-8px 0 16px' }}>
              ⚠ Please select a document type first before choosing a reading method / importing a file
            </p>
          )}

          <div
            className="row"
            style={{ marginBottom: 16, ...(needCategory ? { opacity: 0.45, pointerEvents: 'none' } : {}) }}
          >
            <label className="hint" style={{ fontWeight: 600 }}>
              🧠 Reading Method (OCR Engine)
            </label>
            <OcrProviderSelect providers={providers} value={provider} onChange={setProvider} />
          </div>
          <p className="hint" style={{ margin: '-8px 0 16px' }}>
            {active ? active.desc : ''}
          </p>

          <div
            className={'drop' + (dragOver ? ' over' : '')}
            style={needCategory ? { opacity: 0.45, pointerEvents: 'none' } : undefined}
            onDragEnter={(e) => {
              e.preventDefault();
              setDragOver(true);
            }}
            onDragOver={(e) => e.preventDefault()}
            onDragLeave={(e) => {
              e.preventDefault();
              setDragOver(false);
            }}
            onDrop={(e) => {
              e.preventDefault();
              setDragOver(false);
              const f = e.dataTransfer.files[0];
              if (f) handleFile(f);
            }}
          >
            <div className="big">📤</div>
            <div style={{ margin: '12px 0 4px', fontWeight: 600 }}>Drag and drop a file here, or</div>
            <input
              ref={fileRef}
              type="file"
              accept=".pdf,.jpg,.jpeg,.png,.tif,.tiff"
              hidden
              onChange={(e) => {
                const f = e.target.files?.[0];
                if (f) handleFile(f);
              }}
            />
            <button className="btn primary" onClick={() => fileRef.current?.click()}>
              Select a Document File
            </button>
            <div className="hint" style={{ marginTop: 12 }}>
              Current module: <b>{MODULE_LABEL[mod] || moduleLabel(mod)}</b>
            </div>
            {progress && (
              <div style={{ maxWidth: 440, margin: '18px auto 0' }}>
                <div className="hint">{progress.text}</div>
                <div className="bar">
                  <i style={{ width: progress.pct + '%' }} />
                </div>
              </div>
            )}
            <div className="hint" style={{ marginTop: 18 }}>
              or{' '}
              <a
                href="#"
                onClick={(e) => {
                  e.preventDefault();
                  useSample(0);
                }}
              >
                use sample data
              </a>{' '}
              to test the Mapping / SAP submission steps
            </div>
          </div>
        </div>
      </div>
    </>
  );
}
