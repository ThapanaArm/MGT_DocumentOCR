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
        <h2>🤖 แชทสั่งแก้ไขข้อมูล (AI)</h2>
        <div className="sp" />
        <select
          className="ocr-pick"
          value={chatProvider}
          onChange={(e) => setChatProvider(e.target.value)}
          title="เลือกโมเดล Vision ที่จะใช้อ่านภาพที่แนบมา"
        >
          {chatModels.map((p) => (
            <option key={p.id} value={p.id} title={p.desc}>
              {p.label}
              {p.ready ? '' : ' (ยังไม่ได้ตั้งค่า)'}
            </option>
          ))}
        </select>
        {!ready && <span className="hint">ต้องตั้งค่า API key ของโมเดล Vision อย่างน้อย 1 ตัวก่อนใช้งาน</span>}
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
                  <b>{m.role === 'user' ? 'คุณ' : 'AI'}</b>
                  {imgSrc && <img src={imgSrc} className="chat-img" alt="" />}
                  {m.text && <div>{m.text}</div>}
                </div>
              );
            })
          ) : (
            <p className="hint">
              พิมพ์หรือแนบภาพ บอกจุดที่ OCR อ่านผิดด้วยภาษาธรรมดา หรือถามคำถามเกี่ยวกับเอกสารนี้ — AI
              จะแก้เฉพาะเอกสารนี้ให้ ไม่กระทบเอกสารอื่น
            </p>
          )}
        </div>
        {chatImage && (
          <div className="chat-attach-preview">
            <img src={chatImage} alt="" />
            <span className="hint">แนบภาพแล้ว</span>
            <button className="btn sm ghost" onClick={() => setChatImage(null)}>
              ✕ เอาภาพออก
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
            title="แนบภาพ"
            disabled={!ready}
          >
            📎
          </button>
          <input
            type="text"
            placeholder="เช่น ยอดรวมที่ถูกคือ 25,680 บาท (หรือวางภาพด้วย Ctrl+V)"
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
            ➤ ส่ง
          </button>
        </div>
      </div>
    </div>
  );
}
