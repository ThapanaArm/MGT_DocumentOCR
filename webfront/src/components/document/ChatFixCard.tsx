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
        <h2><i className="fa-solid fa-wand-magic-sparkles" /> ให้ AI ช่วยแก้ข้อมูลเอกสาร</h2>
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
        {!ready && <span className="hint">ยังใช้งานไม่ได้ กรุณาตั้งค่าโมเดล AI อย่างน้อยหนึ่งรายการ</span>}
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
              พิมพ์สิ่งที่ต้องการแก้ หรือแนบภาพประกอบ เช่น “วันที่ส่งสินค้าคือ 20 กันยายน”, “แถวที่ 2 เปลี่ยน Material เป็น
              SOCA01-CN-BG-12”, “เปลี่ยนที่อยู่จัดส่งเป็นสาขาบางนา” หรือ “ลูกค้าไม่ใช่รายนี้ ช่วยหาใหม่จาก SAP” — AI
              จะแก้เฉพาะเอกสารนี้และไม่กระทบเอกสารอื่น
              (การเลือก Material/Ship-to/ลูกค้าผ่านแชทเป็นแค่การเลือกให้เอกสารนี้ ยังไม่บันทึกเป็น Master — ถ้าต้องการบันทึกไว้ใช้ซ้ำ ให้เลือกจากการ์ดด้านบนแทน)
            </p>
          )}
        </div>
        {chatImage && (
          <div className="chat-attach-preview">
            <img src={chatImage} alt="" />
            <span className="hint">แนบรูปภาพแล้ว</span>
            <button className="btn sm ghost" onClick={() => setChatImage(null)}>
              <i className="fa-solid fa-xmark" /> ลบรูป
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
            placeholder="เช่น ยอดรวมที่ถูกต้องคือ 25,680 บาท"
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
            <i className="fa-solid fa-paper-plane" /> ส่งให้ AI
          </button>
        </div>
      </div>
    </div>
  );
}
