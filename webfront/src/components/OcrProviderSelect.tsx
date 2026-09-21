import type { OcrProvider } from '../api/masters';

/* Was a <select> of OCR engines. Per Megachem the reading engine is now LOCKED to Gemini
   everywhere, so there is nothing to choose — this renders a plain, read-only label that just tells
   the user which AI is reading the document. The old props (value / onChange / disabled) are still
   accepted so every caller keeps compiling, but they're intentionally unused now. */
export default function OcrProviderSelect(props: {
  id?: string;
  providers: OcrProvider[];
  value: string;
  onChange: (v: string) => void;
  className?: string;
  disabled?: boolean;
}) {
  const { id, providers, className = 'ocr-engine-label' } = props;

  // Show the Gemini engine's own label when it's in the list, else a sensible constant so the badge
  // is never empty even before the provider list has loaded.
  const gemini = providers.find((p) => p.id.toLowerCase().includes('gemini'));
  const label = gemini?.label || 'Gemini Vision (AI)';

  return (
    <span
      id={id}
      className={className}
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
