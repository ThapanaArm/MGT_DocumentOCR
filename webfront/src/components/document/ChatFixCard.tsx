import { useRef, useState } from 'react';
import type { ChatMessage } from '../../api/documents';
import type { OcrProvider } from '../../api/masters';

/* Ports chatFixCard() — the AI chat that fixes this document's fields. */

const CHAT_MODEL_IDS = ['claude', 'gemini', 'openai'];

function readImageFile(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(reader.result as string);
    reader.onerror = reject;
    reader.readAsDataURL(file);
  });
}

export default function ChatFixCard({
  docId,
  history,
  chatImage,
  setChatImage,
  chatProvider,
  setChatProvider,
  providers,
  onSend,
}: {
  docId: number;
  history: ChatMessage[];
  chatImage: string | null;
  setChatImage: (v: string | null) => void;
  chatProvider: string;
  setChatProvider: (v: string) => void;
  providers: OcrProvider[];
  onSend: (message: string) => void;
}) {
  const [text, setText] = useState('');
  const fileRef = useRef<HTMLInputElement>(null);
  const chatModels = providers.filter((p) => CHAT_MODEL_IDS.includes(p.id));
  const ready = chatModels.some((p) => p.ready);

  async function onFile(file?: File) {
    if (!file) return;
    setChatImage(await readImageFile(file));
  }

  function send() {
    const message = text.trim();
    if (!message && !chatImage) return;
    onSend(message);
    setText('');
  }

  return (
    <div className="card">
      <div className="card-h">
        <h2><i className="fa-solid fa-wand-magic-sparkles" /> Ask AI to fix document data</h2>
        <div className="sp" />
        <select
          className="ocr-pick"
          value={chatProvider}
          onChange={(e) => setChatProvider(e.target.value)}
          title="Select the Vision model used to read the attached image"
        >
          {chatModels.map((p) => (
            <option key={p.id} value={p.id} title={p.desc}>
              {p.label}
              {p.ready ? '' : ' (Not configured)'}
            </option>
          ))}
        </select>
        {!ready && <span className="hint">Not available yet — configure at least one AI model</span>}
      </div>
      <div className="card-b">
        <div className="chat-history">
          {history.length ? (
            history.map((m, i) => {
              const imgSrc =
                m.image ||
                (m.hasImage && m.chatId
                  ? `/api/documents/${docId}/chat/${m.chatId}/image`
                  : '');
              return (
                <div className={'chat-msg ' + m.role} key={i}>
                  <b>{m.role === 'user' ? 'You' : 'AI'}</b>
                  {imgSrc && <img src={imgSrc} className="chat-img" alt="" />}
                  {m.text && <div>{m.text}</div>}
                </div>
              );
            })
          ) : (
            <p className="hint">
              Type what you want to fix, or attach an image — e.g. "delivery date is 20 September", "row 2: change Material to
              SOCA01-CN-BG-12", "change the delivery address to the Bang Na branch", or "this is not the right customer, please search SAP again" — AI
              only edits this document and does not affect others
              (Choosing a Material/Ship-to/customer via chat only applies to this document and is not saved as Master — to save it for reuse, choose from the cards above instead)
            </p>
          )}
        </div>
        {chatImage && (
          <div className="chat-attach-preview">
            <img src={chatImage} alt="" />
            <span className="hint">Image attached</span>
            <button className="btn sm ghost" onClick={() => setChatImage(null)}>
              <i className="fa-solid fa-xmark" /> Remove image
            </button>
          </div>
        )}
        <div className="row" style={{ marginTop: 10 }}>
          <input
            ref={fileRef}
            type="file"
            accept="image/*"
            hidden
            onChange={(e) => onFile(e.target.files?.[0])}
          />
          <button
            className="btn sm ghost"
            onClick={() => fileRef.current?.click()}
            title="Attach image"
            disabled={!ready}
          >
            <i className="fa-solid fa-paperclip" />
          </button>
          <input
            type="text"
            placeholder="e.g. the correct total is 25,680 THB"
            style={{ flex: 1 }}
            value={text}
            disabled={!ready}
            onChange={(e) => setText(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter') send();
            }}
            onPaste={async (e) => {
              const item = [...(e.clipboardData?.items || [])].find((it) =>
                it.type.startsWith('image/'),
              );
              if (!item) return;
              e.preventDefault();
              const f = item.getAsFile();
              if (f) setChatImage(await readImageFile(f));
            }}
          />
          <button className="btn primary" onClick={send} disabled={!ready}>
            <i className="fa-solid fa-paper-plane" /> Send to AI
          </button>
        </div>
      </div>
    </div>
  );
}
