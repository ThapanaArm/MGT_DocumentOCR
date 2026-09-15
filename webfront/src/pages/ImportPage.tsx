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

// Deprecated. The backend no longer reads any "user" value sent by the client — it stamps the
// identity from the validated Entra ID token instead, so whatever is passed here is discarded.
// Left in place only so the existing call signatures keep compiling; remove it together with the
// `user` parameters in api/documents.ts.
const USER = '(ignored by the server)';

export default function ImportPage() {
  const { module } = useParams<{ module: ModuleCode }>();
  const mod = (module ?? 'AP') as ModuleCode;
  // AP = the single liability-recording (การตั้งหนี้) reading page; it uses the Document Type dropdown.
  const isInvoice = mod === 'AP';
  const navigate = useNavigate();
  const { guard, showToast } = useAppState();
  const { ocrProviders, loadOcrProviders, apDocCategories, loadApDocCategories } = useMeta();

  const [provider, setProvider] = useState('auto');
  const [category, setCategory] = useState('');
  const [dragOver, setDragOver] = useState(false);
  const [progress, setProgress] = useState<{ text: string; pct: number } | null>(null);
  const fileRef = useRef<HTMLInputElement>(null);
  const [pwPrompt, setPwPrompt] = useState<{ file: File; wrong: boolean } | null>(null);
  const [pwValue, setPwValue] = useState('');

  useEffect(() => {
    loadOcrProviders();
    if (isInvoice) loadApDocCategories();
    setCategory('');
  }, [mod, loadOcrProviders, loadApDocCategories]);

  const needCategory = isInvoice && !category;
  const providers = ocrProviders ?? [];
  const active = providers.find((p) => p.id === provider) || providers[0];

  async function doUpload(file: File, password?: string) {
    if (isInvoice && !category) {
      showToast('Please select a document type first');
      return;
    }
    const fd = new FormData();
    fd.append('module', mod);
    fd.append('user', USER);
    fd.append('ocr_', provider);
    fd.append('file', file);
    fd.append('apDocCategory', category || '');
    if (password) fd.append('password', password);
    setProgress({ text: 'Uploading ' + file.name + ' …', pct: 35 });
    try {
      setProgress({ text: 'Reading document (' + provider + ') …', pct: 70 });
      const doc = await uploadDocument(fd);
      setProgress(null);
      setPwPrompt(null);
      setPwValue('');
      if (doc.provider === 'failed')
        showToast('Failed to read document — attach an image in the AI chat on the document page for help filling it in');
      else showToast('Document read successfully (' + doc.provider + ') — found ' + doc.lines.length + ' items');
      navigate('/doc/' + doc.docId);
    } catch (e) {
      setProgress(null);
      const msg = e instanceof Error ? e.message : String(e);
      // Encrypted PDF: reveal a password field and let the user retry with the open password.
      if (msg === 'PDF_PASSWORD_REQUIRED' || msg === 'PDF_PASSWORD_WRONG') {
        setPwPrompt({ file, wrong: msg === 'PDF_PASSWORD_WRONG' });
        return;
      }
      showToast(msg);
    }
  }

  function handleFile(file: File) {
    setPwPrompt(null);
    setPwValue('');
    void doUpload(file);
  }

  async function useSample(i: number) {
    if (isInvoice && !category) {
      showToast('Please select a document type first');
      return;
    }
    const doc = await guard(() =>
      sampleDocument({ module: mod, index: i, user: USER, apDocCategory: category }),
    );
    if (doc) {
      showToast('Document created in the system (DocId ' + doc.docId + ')');
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
          {isInvoice && (
            <div className="row" style={{ marginBottom: 16 }}>
              <label className="hint" style={{ fontWeight: 600 }}>
                <i className="fa-solid fa-clipboard-list" /> Document Type
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
              <i className="fa-solid fa-triangle-exclamation" /> Please select a document type first before choosing a reading method / importing a file
            </p>
          )}

          <div
            className="row"
            style={{ marginBottom: 16, ...(needCategory ? { opacity: 0.45, pointerEvents: 'none' } : {}) }}
          >
            <label className="hint" style={{ fontWeight: 600 }}>
              <i className="fa-solid fa-brain" /> Reading Method (OCR Engine)
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
            <div className="big"><i className="fa-solid fa-cloud-arrow-up" /></div>
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
              Current module: <b>{mod === 'AP' ? 'Invoice' : MODULE_LABEL[mod] || moduleLabel(mod)}</b>
            </div>
            {progress && (
              <div style={{ maxWidth: 440, margin: '18px auto 0' }}>
                <div className="hint">{progress.text}</div>
                <div className="bar">
                  <i style={{ width: progress.pct + '%' }} />
                </div>
              </div>
            )}
            {pwPrompt && (
              <div style={{ maxWidth: 440, margin: '18px auto 0', textAlign: 'left' }}>
                <label className="hint" style={{ fontWeight: 600, display: 'block', marginBottom: 6 }}>
                  <i className="fa-solid fa-lock" /> This PDF is password-protected — enter the document open password
                </label>
                <div style={{ display: 'flex', gap: 8 }}>
                  <input
                    type="password"
                    autoFocus
                    value={pwValue}
                    onChange={(e) => setPwValue(e.target.value)}
                    onKeyDown={(e) => {
                      if (e.key === 'Enter' && pwValue) void doUpload(pwPrompt.file, pwValue);
                    }}
                    placeholder="Document password"
                    style={{ flex: 1 }}
                  />
                  <button className="btn primary" disabled={!pwValue} onClick={() => void doUpload(pwPrompt.file, pwValue)}>
                    Unlock &amp; read
                  </button>
                </div>
                {pwPrompt.wrong && (
                  <p className="hint" style={{ color: 'var(--red)', margin: '6px 0 0' }}>
                    <i className="fa-solid fa-triangle-exclamation" /> Incorrect password — please try again
                  </p>
                )}
                <p className="hint" style={{ margin: '6px 0 0', fontSize: 12 }}>{pwPrompt.file.name}</p>
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
