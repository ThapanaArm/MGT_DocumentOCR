import type { OcrProvider } from '../api/masters';

/* Ports ocrProviderSelect() — a <select> of OCR engines with ready flags. */
export default function OcrProviderSelect({
  id,
  providers,
  value,
  onChange,
  className = 'ocr-pick',
  disabled,
}: {
  id?: string;
  providers: OcrProvider[];
  value: string;
  onChange: (v: string) => void;
  className?: string;
  disabled?: boolean;
}) {
  // Locked to Gemini: only the Gemini engine is offered in the dropdown (per Megachem). Falls back
  // to the full list only if no Gemini engine is present, so the control is never empty.
  const geminiOnly = providers.filter((p) => p.id.toLowerCase().includes('gemini'));
  const opts = geminiOnly.length ? geminiOnly : providers;
  return (
    <select
      id={id}
      className={className}
      value={value}
      disabled={disabled}
      onChange={(e) => onChange(e.target.value)}
    >
      {opts.map((p) => (
        <option key={p.id} value={p.id} title={p.desc}>
          {p.label}
          {p.ready ? '' : ' (Not configured)'}
        </option>
      ))}
    </select>
  );
}
