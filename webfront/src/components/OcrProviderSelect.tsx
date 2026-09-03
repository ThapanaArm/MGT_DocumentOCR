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
  return (
    <select
      id={id}
      className={className}
      value={value}
      disabled={disabled}
      onChange={(e) => onChange(e.target.value)}
    >
      {providers.map((p) => (
        <option key={p.id} value={p.id} title={p.desc}>
          {p.label}
          {p.ready ? '' : ' (Not configured)'}
        </option>
      ))}
    </select>
  );
}
