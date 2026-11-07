import type { OcrProvider } from '../api/masters';

/* Reading-engine picker. Every module reads with Gemini Vision only — ChatGPT Vision was removed
   (4 Nov 2026) so the reading cost is billed on one provider. Engines without a configured API key are disabled.
   Callers that pass no `choices` (Compare modal) keep the old read-only Gemini label. */
export const READ_ENGINE_IDS = ['gemini'];
export const AP_READ_ENGINE_IDS = ['gemini'];

export default function OcrProviderSelect(props: {
  id?: string;
  providers: OcrProvider[];
  value: string;
  onChange: (v: string) => void;
  className?: string;
  disabled?: boolean;
  choices?: string[];
}) {
  const { id, providers, value, onChange, disabled, choices } = props;

  if (choices && choices.length) {
    const opts = choices
      .map((c) => providers.find((p) => p.id === c))
      .filter((p): p is OcrProvider => !!p);
    if (opts.length > 1) {
      const current = opts.some((o) => o.id === value) ? value : 'gemini';
      return (
        <select
          id={id}
          className={props.className || 'ocr-pick'}
          value={current}
          disabled={disabled}
          onChange={(e) => onChange(e.target.value)}
          title={opts.find((o) => o.id === current)?.desc}
        >
          {opts.map((o) => (
            <option key={o.id} value={o.id} disabled={!o.ready}>
              {o.label}
              {o.ready ? '' : ' — not set up on server'}
            </option>
          ))}
        </select>
      );
    }
  }

  // Read-only label (Gemini). Exact id match so "paddle_gemini" is never mistaken for Gemini.
  const gemini = providers.find((p) => p.id.toLowerCase() === 'gemini');
  const label = gemini?.label || 'Gemini Vision (AI)';

  return (
    <span
      id={id}
      className={props.className || 'ocr-engine-label'}
      title={gemini?.desc || 'AI engine used to read this document'}
      style={{
        display: 'inline-flex',
        alignItems: 'center',
        gap: 6,
        padding: '4px 10px',
        borderRadius: 999,
        background: 'var(--chip-bg, #eef2ff)',
        color: 'var(--chip-fg, #3730a3)',
        border: '1px solid var(--chip-bd, #c7d2fe)',
        fontSize: 13,
        fontWeight: 600,
        whiteSpace: 'nowrap',
      }}
    >
      <i className="fa-solid fa-robot" /> {label}
    </span>
  );
}
